using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace AudioDirigent;

internal static class Program
{
	[DllImport("kernel32.dll")]
	private static extern bool AttachConsole(int processId);

	[STAThread]
	private static int Main(string[] args)
	{
		var mode = args.FirstOrDefault() ?? "";

		// Application создаётся всегда: словари строк грузятся по pack-схеме, а её включает он.
		var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
		Localization.Initialize();
		Theme.Initialize();

		// Программа живёт в трее сутками, и падение фонового потока раньше уносило её молча:
		// пользователь замечал это только по мёртвому звуку после сна.
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Journal.Add("langLogFatal", e.ExceptionObject);

		// Режимы перечислены один раз: список «это консольная команда» отдельно от разбора
		// расходился бы молча — неизвестный ключ поднимал бы трей вместо ответа в консоль.
		Func<int>? command = mode switch
		{
			"--list" => ListMode,
			"--once" => OnceMode,
			"--test" => SelfCheck,
			"--devices" => DevicesMode,
			"--recover" => RecoverMode,
			"--autostart" => () => AutostartMode(args.Skip(1).FirstOrDefault()),
			"--help" or "-h" or "/?" => Usage,
			_ => null,
		};

		if (command is not null)
		{
			AttachConsole(-1 /* ATTACH_PARENT_PROCESS */);
			Console.OutputEncoding = Encoding.UTF8;
			try
			{
				return command();
			}
			catch (Exception ex)
			{
				Console.WriteLine(Localization.Format("langCliError", ex));
				return 1;
			}
		}

		using var single = new Mutex(true, "AudioDirigent", out var owned);
		if (!owned)
		{
			return 0; // уже запущен — второй экземпляр не нужен
		}

		Journal.Trim();

		using var tray = new TrayIcon(showWindow: mode != "--tray");

		Autostart.Repair();

		// Ошибка в обработчике UI не должна убивать программу: она живёт в трее часами.
		app.DispatcherUnhandledException += (_, e) =>
		{
			Journal.Add("langLogUiError", e.Exception);
			e.Handled = true;
		};

		app.Run();
		return 0;
	}

	private static int Usage()
	{
		Console.WriteLine(Localization.Get("langCliUsage"));
		return 2;
	}

	private static int AutostartMode(string? action)
	{
		if (action is "on" or "off")
		{
			if (Autostart.Set(action == "on") is { } error)
			{
				Console.WriteLine(Localization.Format("langCliError", error));
				return 1;
			}
		}

		Console.WriteLine(Autostart.IsEnabled()
			? Localization.Format("langCliTaskPresent", Autostart.TaskName, Environment.ProcessPath)
			: Localization.Format("langCliTaskAbsent", Autostart.TaskName));

		return 0;
	}

	// Все HID-интерфейсы системы: с этого начинается разбор протокола приёмника, из
	// которого потом складывается блок probe в config.json. Состояние беспроводной
	// гарнитуры больше взять неоткуда — Windows о нём не знает.
	private static int DevicesMode()
	{
		var devices = DeviceProbe.Enumerate().ToList();
		if (devices.Count == 0)
		{
			Console.WriteLine(Localization.Get("langCliNoHid"));
			return 0;
		}

		Console.WriteLine(Localization.Get("langCliHidHeader"));
		foreach (var device in devices)
		{
			Console.WriteLine($"{device.VendorId:X4} {device.ProductId:X4} {device.UsagePage,9:X4} {device.Usage,5:X4} " +
				$"{device.InputLength,2} {device.OutputLength,3}  {device.Path}");
		}

		using var probe = DeviceProbe.Open();
		if (probe is null)
		{
			Console.WriteLine(Environment.NewLine + Localization.Get("langCliNoProbe"));
			return 0;
		}

		var response = probe.Query();
		Console.WriteLine(Environment.NewLine + Localization.Format("langCliProbeReply",
			response is null ? Localization.Get("langCliNoReply") : Convert.ToHexString(response)));
		Console.WriteLine(Localization.Format("langCliProbeOn",
			probe.IsOn()?.ToString() ?? Localization.Get("langCliUnknown")));

		return 0;
	}

	// Ручной вызов того же восстановления, что идёт после пробуждения: удобно, когда
	// звук уже отвалился, а программа в трее работает без прав администратора.
	private static int RecoverMode()
	{
		using var switcher = new Switcher();
		switcher.Logged += entry => Console.WriteLine(entry.Render());

		// Знакомимся с устройствами и сразу спрашиваем, чего не хватает: в отдельном
		// запуске программа ещё не видела, что было живо до того, как звук пропал.
		switcher.Start();
		switcher.Recover();

		return switcher.Missing().Count == 0 ? 0 : 1;
	}

	private static int ListMode()
	{
		foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
		{
			var current = Audio.GetDefault(flow, ERole.Multimedia);
			Console.WriteLine(Localization.Get(flow == EDataFlow.Capture ? "langInput" : "langOutput"));

			// Шина и тип печатаются рядом с именем: по ним программа отличает воткнутое от
			// подключившегося само и музыкальный профиль от телефонного, так что при разборе
			// странного выбора смотреть надо именно на них.
			foreach (var device in Audio.ListDevices(flow))
			{
				var marker = device.Id == current?.Id ? $"  {Localization.Get("langCliDefaultMarker")}" : "";
				Console.WriteLine($"{device.State,-12} {device.Bus,-10} {device.Form,-11} {device.Name}{marker}");
			}

			Console.WriteLine();
		}

		return 0;
	}

	private static int OnceMode()
	{
		using var switcher = new Switcher();
		switcher.Logged += entry => Console.WriteLine(entry.Render());

		// Именно Start, а не Apply: без опроса приёмника правило не знает, выключена ли
		// беспроводная гарнитура, и режим отработал бы иначе, чем программа в трее.
		switcher.Start();
		return 0;
	}

	// Правило выбора на живых устройствах не проверить: понадобилась бы гарнитура Bluetooth,
	// подключённая сразу двумя профилями, и виртуальный драйвер вдобавок. Считаем его на
	// выдуманных — ошибка в приоритетах тихая и обнаруживается уже пропавшим звуком.
	// Имена устройств здесь — данные, а не текст для чтения: по ним ищутся паттерны.
	private static string[] CheckRule()
	{
		static AudioEndpoint Device(string id, string name, string bus) =>
			new(id, name, DeviceState.Active, EDataFlow.Render, FormFactor.Unknown, bus, null, null);

		// Гарнитура Bluetooth приходит в систему двумя устройствами с почти одинаковыми
		// именами: музыкальным профилем и телефонным. Один и тот же приоритет ловит оба.
		var music = Device("1", "Wireless Headset Stereo", "BTHENUM");
		var phone = Device("2", "Wireless Headset Hands-Free AG", "BTHHFENUM");
		var mixer = Device("3", "Wireless Headset (Virtual Cable)", "SWD");
		var speakers = Device("4", "Speakers (Realtek(R) Audio)", "HDAUDIO");
		var blocked = Device("5", "Speakers (Monitor via HDMI)", "HDAUDIO");
		var unknown = Device("6", "USB Microphone", "USB");
		var config = new Config(["Wireless Headset", "Realtek"], ["HDMI"]);

		(bool Ok, string Rule)[] checks =
		[
			(config.SelectBest([speakers, music]) == music, "langRulePriorityOrder"),
			(config.SelectBest([phone, music, speakers]) == music, "langRuleMusicOverPhone"),
			(config.SelectBest([mixer, music]) == music, "langRulePhysicalOverVirtual"),
			(config.SelectBest([mixer, speakers]) == mixer, "langRuleVirtualStillWins"),
			(config.SelectBest([blocked]) is null, "langRuleBlockedNever"),
			(config.Promote(speakers).Rank(speakers) == 0, "langRulePromoteFirst"),
			(config.Promote(music).Priority.Count == config.Priority.Count, "langRulePromoteReplaces"),
			(config.Prioritise(music).Priority.SequenceEqual(config.Priority), "langRulePatternSurvivesPriority"),
			(config.Prioritise(unknown).Priority.Contains(unknown.Name), "langRuleUnlistedToPriority"),
			(config.Block(blocked).Rank(music) == 0, "langRuleBlockKeepsPriority"),
			(config.Block(blocked).Blocked.SequenceEqual(config.Blocked), "langRulePatternSurvivesBlock"),
			(config.Block(unknown).Blocked.Contains(unknown.Name), "langRuleUnlistedToBlocked"),
		];

		return [.. checks.Where(check => !check.Ok).Select(check => Localization.Get(check.Rule))];
	}

	// Забытый в одном словаре ключ виден только пользователю с этим языком — вместо
	// строки он получит её имя. Сверяем наборы ключей между всеми языками.
	private static string[] CheckLanguages()
	{
		var byCode = Localization.Codes.ToDictionary(
			code => code,
			code => new ResourceDictionary { Source = new Uri($"Lang/Lang.{code}.xaml", UriKind.Relative) }
				.Keys.Cast<string>()
				.ToHashSet());

		var everything = byCode.Values.SelectMany(keys => keys).ToHashSet();

		return [.. byCode
			.Where(language => language.Value.Count != everything.Count)
			.Select(language => Localization.Format("langCliLanguageMissing",
				language.Key, string.Join(", ", everything.Except(language.Value))))];
	}

	// Запись журнала уходит в файл ключом с аргументами, а текст собирается при показе.
	// Сломается разбор строки — старые записи молча превратятся в собственные ключи.
	private static bool CheckJournal()
	{
		var written = Journal.Add("langLogBuild", Build.Version);
		var read = Journal.Recent(1).LastOrDefault();

		return read is not null && read.Key == written.Key && read.Arguments.SequenceEqual(written.Arguments);
	}

	// Тихое уведомление держится на приватных полях WinForms: сменят тип или имя — звук
	// вернётся молча. Значок показывается на миг: без этого окна у него ещё нет.
	private static bool CheckBalloon()
	{
		using var icon = new NotifyIcon { Icon = SystemIcons.Application, Visible = true };
		var ready = Balloon.Ready(icon);
		icon.Visible = false;

		return ready;
	}

	// Звук гарнитуры сидит на одном интерфейсе составного устройства, а её управляющий HID —
	// на соседнем. Перезапускать надо их общего родителя, иначе звук вернётся, а приложение
	// производителя будет писать, что гарнитуры нет.
	private static bool CheckParent()
	{
		List<string> nodes =
		[
			@"USB\VID_03F0&PID_089D\000000000000",
			@"USB\VID_03F0&PID_089D&MI_00\6&2afd919&0&0000",
			@"ROOT\{BBC5BC33-C330-4E72-9FAA-D2E69829A7C1}\0000",
		];

		return Recovery.Parent(nodes[1], nodes) == nodes[0]
			&& Recovery.Parent(nodes[2], nodes) == nodes[2]
			&& Recovery.Parent(nodes[1], [nodes[1]]) == nodes[1];
	}

	// Карточка подключения собирается из рисунка по типу устройства, а имя рисунка — строка.
	// Разъехавшись со словарём разметки, она молчала бы до первого подключения устройства,
	// то есть до чужой машины. Собираем карточку для каждого типа, не показывая её.
	private static string? CheckPopup()
	{
		using var switcher = new Switcher();
		var popup = new DevicePopup(switcher);

		try
		{
			foreach (var form in Enum.GetValues<FormFactor>())
			{
				popup.Fill(
					new AudioEndpoint("1", "Device", DeviceState.Active, EDataFlow.Render, form, "USB", null, null),
					arrived: true,
					becameDefault: false);
			}

			return null;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}
		finally
		{
			popup.Close();
		}
	}

	// Настройки уходят в файл через отражение: переименованное свойство или запись без
	// подходящего конструктора ломают чтение молча, и настройки просто «забываются».
	private static bool CheckStore()
	{
		var sample = new Store.Data
		{
			Language = "uk",
			Hotkeys = true,
			Output = new(["HyperX"], ["NGENUITY"]),
			Volume = [new VolumeRule("HyperX", 25)],
			UsbPowerHeld = [@"USB\ROOT_HUB30\4&1d821119&0&0_0"],
		};

		var copy = Store.Roundtrip(sample);

		return copy is not null
			&& copy.Language == sample.Language
			&& copy.Hotkeys == sample.Hotkeys
			&& copy.Output is { } output
			&& output.Priority.SequenceEqual(sample.Output.Priority)
			&& output.Blocked.SequenceEqual(sample.Output.Blocked)
			&& copy.Volume.SequenceEqual(sample.Volume)
			&& copy.UsbPowerHeld.SequenceEqual(sample.UsbPowerHeld);
	}

	// Громкость устройства идёт через отдельный интерфейс, который тоже надо активировать.
	// Ставим текущее значение обратно: проверка ничего не меняет, но ловит отказ активации.
	private static bool CheckVolume(AudioEndpoint device) =>
		Audio.GetVolume(device.Id) is { } level
			&& Audio.SetVolume(device.Id, level)
			&& Audio.GetVolume(device.Id) == level;

	// Единственная проверка, которая падает, если недокументированный IPolicyConfig
	// перестал работать: переключаем на другое активное устройство и возвращаем обратно.
	private static int SelfCheck()
	{
		if (CheckLanguages() is { Length: > 0 } missing)
		{
			Console.WriteLine(Localization.Format("langCliLanguageFail", string.Join("; ", missing)));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliLanguageOk"));

		if (CheckRule() is { Length: > 0 } broken)
		{
			Console.WriteLine(Localization.Format("langCliRuleFail", string.Join("; ", broken)));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliRuleOk"));

		if (!CheckJournal())
		{
			Console.WriteLine(Localization.Get("langCliJournalFail"));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliJournalOk"));

		if (!CheckStore())
		{
			Console.WriteLine(Localization.Get("langCliStoreFail"));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliStoreOk"));

		if (!CheckBalloon())
		{
			Console.WriteLine(Localization.Get("langCliBalloonFail"));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliBalloonOk"));

		if (!CheckParent())
		{
			Console.WriteLine(Localization.Get("langCliParentFail"));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliParentOk"));

		if (CheckPopup() is { } popupError)
		{
			Console.WriteLine(Localization.Format("langCliPopupFail", popupError));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliPopupOk"));

		var active = Audio.ListDevices(EDataFlow.Render, DeviceState.Active);
		var original = Audio.GetDefault(EDataFlow.Render, ERole.Multimedia);
		Console.WriteLine(Localization.Format("langCliActiveDevices", active.Count, original?.Name ?? Localization.Get("langNone")));

		if (original is null)
		{
			Console.WriteLine(Localization.Get("langCliNoActive"));
			return 0;
		}

		if (!CheckVolume(original))
		{
			Console.WriteLine(Localization.Get("langCliVolumeFail"));
			return 1;
		}

		Console.WriteLine(Localization.Get("langCliVolumeOk"));

		var other = active.FirstOrDefault(d => d.Id != original.Id);
		if (other is null)
		{
			// Переключать не на что: проверяем хотя бы, что IPolicyConfig жив и вызов проходит.
			Audio.SetDefault(original.Id);
			if (Audio.GetDefault(EDataFlow.Render, ERole.Multimedia)?.Id != original.Id)
			{
				Console.WriteLine(Localization.Get("langCliSameFail"));
				return 1;
			}

			Console.WriteLine(Localization.Get("langCliPartialOk"));
			return 0;
		}

		Audio.SetDefault(other.Id);
		var afterSwitch = Audio.GetDefault(EDataFlow.Render, ERole.Multimedia);

		Audio.SetDefault(original.Id);
		var afterRestore = Audio.GetDefault(EDataFlow.Render, ERole.Multimedia);

		if (afterSwitch?.Id != other.Id)
		{
			Console.WriteLine(Localization.Format("langCliSwitchFail", other.Name, afterSwitch?.Name));
			return 1;
		}

		if (afterRestore?.Id != original.Id)
		{
			Console.WriteLine(Localization.Format("langCliRestoreFail", original.Name, afterRestore?.Name));
			return 1;
		}

		Console.WriteLine(Localization.Format("langCliSwitchOk", other.Name, original.Name));
		return 0;
	}
}
