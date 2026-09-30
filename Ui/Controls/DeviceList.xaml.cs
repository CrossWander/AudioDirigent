using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AudioDirigent;

/// <summary>
/// Ручка, которую отдало само устройство: ползунок усиления или выключатель автоподстройки.
/// Подпись берём у драйвера — той же, что стоит в параметрах звука Windows.
/// </summary>
internal sealed record KnobRow(MicrophoneKnob Knob, double Step, bool Snap)
{
	public string Name => Knob.Name;

	public bool IsLevel => Knob.Kind == MicrophoneKnobKind.Level;

	public double Minimum => Knob.Minimum;

	public double Maximum => Knob.Maximum;

	// Привязки применяются и при первом показе, и при пересборке строки. Правка — это только
	// то, что отличается от уже стоящего, а знать это может лишь сама ручка.
	public double Value { get; set; }

	public bool On { get; set; }
}

/// <summary>
/// Список устройств одного направления и всё, что с ними делают. Строки живут дольше одного
/// обновления: список сверяется по номеру устройства и правит существующие строки вместо того,
/// чтобы собирать их заново, — иначе выделение слетало бы на каждое событие Core Audio.
/// </summary>
public partial class DeviceList : UserControl
{
	// Список держит и устройства, и заголовки разделов: порядок между ними и есть
	// смысл окна, и собирать его в двух разных местах нельзя.
	private readonly ObservableCollection<object> _items = [];

	// Строки живут дольше обновления: иначе раскрытая строка закрывалась бы на каждое
	// событие Core Audio, а их приходит по нескольку в секунду.
	private readonly Dictionary<string, DeviceRow> _byId = new(StringComparer.Ordinal);

	private readonly SectionRow _silent = new();
	private readonly SectionRow _banned = new();

	private Switcher? _switcher;
	private EDataFlow _flow = EDataFlow.Render;
	private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(60) };

	private DeviceRow? _open;
	private Meter? _signal;
	private bool _filling;

	// Проверка: докуда слушаем и что самое громкое услышали.
	private DateTime _until;
	private double _loudest;

	// Ниже этого уровня шкала уже ничего не различает — там комнатная тишина.
	private const double _floor = -60;

	// Пороги приговора по пику речи. Ниже -50 dB нет и речи — там только шум комнаты;
	// выше -2 сигнал упирается в предел шкалы и начинает трещать.
	private const double _nothing = -50;
	private const double _quiet = -30;
	private const double _clipping = -2;

	// Пять секунд: меньше — не успеешь сказать фразу, больше — стоишь и ждёшь.
	private static readonly TimeSpan _listen = TimeSpan.FromSeconds(5);

	// Привязка слушает дольше обычного обхода: объявления приходят пачками с паузами, и
	// за пару секунд ближайшее устройство можно просто не застать.
	private const int _bindSeconds = 10;

	public DeviceList()
	{
		InitializeComponent();
		Rows.ItemsSource = _items;
		_meter.Tick += (_, _) => ShowPeak();
	}

	/// <summary>Направление сменили — шапке окна пора показать другое устройство.</summary>
	internal event Action<EDataFlow>? FlowChanged;

	internal EDataFlow Flow => _flow;

	internal void Attach(Switcher switcher) => _switcher = switcher;

	internal void Refill()
	{
		if (_switcher is not { } switcher)
		{
			return;
		}

		var config = switcher.Rules.For(_flow);
		var current = Endpoints.Current(_flow);
		var devices = Endpoints.All(_flow);

		// Правило — это и есть ответ приложения на вопрос «какое устройство вы имели в виду»,
		// и читается он сверху вниз. Раньше список шёл по состоянию, а места правила были
		// рассыпаны по нему номерами: чтобы увидеть правило, его приходилось собирать глазами.
		var ruled = devices
			.Where(device => !config.Blocks(device) && config.Rank(device) >= 0)
			.OrderBy(config.Rank)
			.ThenBy(device => device.Name)
			.ToList();

		var banned = devices.Where(config.Blocks).OrderBy(device => device.Name).ToList();
		var rest = devices.Except(ruled).Except(banned).OrderBy(device => device.Name).ToList();

		// Живое устройство, о котором правил нет, — это вопрос, а вопрос не прячут под свёртку:
		// он стоит сразу под правилом, там же, где на него отвечают.
		var asking = rest.Where(device => device.State == DeviceState.Active).ToList();
		var silent = rest.Except(asking).ToList();

		_silent.Title = Localization.Get("langSectionQuiet");
		_silent.Count = silent.Count;
		_banned.Title = Localization.Get("langSectionBlocked");
		_banned.Count = banned.Count;

		List<object> wanted = [.. ruled.Concat(asking).Select(Row)];

		if (silent.Count > 0)
		{
			wanted.Add(_silent);
			wanted.AddRange(_silent.Open ? silent.Select(Row) : []);
		}

		if (banned.Count > 0)
		{
			wanted.Add(_banned);
			wanted.AddRange(_banned.Open ? banned.Select(Row) : []);
		}

		// Перебор строк меняет выделение, а на выделение подвешено раскрытие: пока идёт
		// сверка, оно не должно принимать перестановку за выбор человека.
		_filling = true;
		Sync(wanted);
		_filling = false;

		// Раскрытое устройство могло и пропасть — тогда отпускаем и поток с него.
		if (_open is { } open && !_items.Contains(open))
		{
			Close();
		}

		var ranks = ruled.Select(config.Rank).ToList();

		foreach (var row in _items.OfType<DeviceRow>())
		{
			Describe(row, config, current, ranks);
		}
	}

	/// <summary>Строка этого устройства — прежняя, если она уже была.</summary>
	private DeviceRow Row(AudioEndpoint device)
	{
		if (_byId.TryGetValue(device.Id, out var row))
		{
			row.Update(device);

			return row;
		}

		return _byId[device.Id] = new DeviceRow(device);
	}

	/// <summary>
	/// Привести список к желаемому, двигая то, что уже есть. Собрать заново было бы короче,
	/// но тогда на каждое событие Core Audio слетала бы прокрутка и закрывалась раскрытая
	/// строка — а событий приходит по нескольку в секунду.
	/// </summary>
	private void Sync(List<object> wanted)
	{
		for (var index = 0; index < wanted.Count; index++)
		{
			var at = _items.IndexOf(wanted[index]);

			if (at < 0)
			{
				_items.Insert(index, wanted[index]);
			}
			else if (at != index)
			{
				_items.Move(at, index);
			}
		}

		while (_items.Count > wanted.Count)
		{
			_items.RemoveAt(_items.Count - 1);
		}

		// Устройство исчезло насовсем — забываем и его строку, иначе она вернулась бы
		// раскрытой через час после того, как гарнитуру унесли.
		var alive = _items.OfType<DeviceRow>().Select(row => row.Device.Id).ToHashSet(StringComparer.Ordinal);

		foreach (var id in _byId.Keys.Where(id => !alive.Contains(id)).ToList())
		{
			_byId.Remove(id);
		}
	}

	private void Describe(DeviceRow row, Config config, AudioEndpoint? current, List<int> ranks)
	{
		var device = row.Device;
		var rank = config.Rank(device);
		var blocked = config.Blocks(device);
		var active = device.State == DeviceState.Active;

		// Значок дают правила, а правило — это фрагмент имени. Пока его не назвать, непонятно,
		// почему у устройства номер и что именно снимет «Сбросить».
		var rule = blocked
			? config.Blocked.First(pattern => Config.Matches(device, pattern))
			: rank >= 0 ? config.Priority[rank] : "";

		// Живое устройство, о котором правила не знают ничего, помечается вместо номера:
		// решение по нему ещё не принято. Молчащие не помечаем — список и так из них наполовину.
		var undecided = active && !blocked && rank < 0;
		var level = Volume.For(device);

		// Заряд сообщает меньшинство устройств, и «нет заряда» — обычное состояние, а не
		// сбой: пустое место честнее прочерка, который читался бы как «ноль процентов».
		row.Charge = Battery.Of(device.Node)?.Text ?? "";

		// Кнопка привязки предлагается только там, где она может что-то дать: устройство
		// по радио, а числа до сих пор нет. Как только заряд появился, кнопка уходит.
		row.CanBind = row.Charge.Length == 0 && Battery.Mac(device.Node) is not null
			&& Store.Current.Beacons.Count > 0;

		row.Active = active;
		row.Current = device.Id == current?.Id;
		row.State = Describe(device.State);
		row.Mark = blocked ? RowMark.Blocked : rank >= 0 ? RowMark.Priority : undecided ? RowMark.Undecided : RowMark.None;
		row.Badge = blocked ? "✕" : rank >= 0 ? (rank + 1).ToString() : undecided ? "+" : "";

		// Место в очереди двигают стрелками рядом с ним. Гаснут они у краёв, но считать надо
		// не по строкам, а по местам правила: одно место может ловить два устройства сразу,
		// и стрелка у второго из них двигала бы их оба.
		row.InRule = !blocked && rank >= 0;
		row.CanRaise = row.InRule && ranks.Any(other => other < rank);
		row.CanLower = row.InRule && ranks.Any(other => other > rank);

		// «Сделать основным» — то же предложение, что и в карточке над треем: она живёт
		// четыре секунды, пропустить её нормально, и решение должно оставаться под рукой.
		row.CanMakeMain = active && !blocked && rank < 0 && !row.Current;

		// Связь поднимают только у Bluetooth: у остального за это отвечает разъём.
		row.CanLink = device.Bluetooth && device.State is DeviceState.Active or DeviceState.Unplugged;
		row.LinkText = Localization.Get(active ? "langDisconnect" : "langConnect");
		row.BadgeHint = blocked ? Localization.Format("langBadgeBlocked", rule)
			: rank >= 0 ? Localization.Format("langBadgePriority", rank + 1, rule)
			: undecided ? Localization.Get("langBadgeUndecided") : null;

		// Прочерк вместо пустоты: плашка — единственный способ задать уровень, и она должна
		// быть видна и там, где закреплять ещё нечего.
		row.Pinned = level is not null;
		row.Level = level is null ? "—" : $"{level.Percent}%";
		row.LevelHint = level is null
			? Localization.Get("langLevelNone")
			: Localization.Format("langLevelPinned", level.Percent, level.Match);
	}

	private static string Describe(DeviceState state) => Localization.Get(state switch
	{
		DeviceState.Active => "langStateActive",
		DeviceState.Disabled => "langStateDisabled",
		DeviceState.Unplugged => "langStateUnplugged",
		_ => "langStateNotPresent",
	});

	private void OnSelected(object sender, SelectionChangedEventArgs e)
	{
		// Заголовок раздела выделять не за что: он не устройство, и держать на нём рамку
		// значило бы обещать действия, которых у него нет.
		if (Rows.SelectedItem is SectionRow)
		{
			Rows.SelectedItem = null;

			return;
		}

		if (!_filling)
		{
			Expand(Rows.SelectedItem as DeviceRow);
		}
	}

	private void OnSection(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is SectionRow section)
		{
			section.Open = !section.Open;
			Refill();
		}
	}

	// Шеврон делает то же самое: он нужен, чтобы раскрытие было видно, а не угадывалось.
	private void OnChevron(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
		{
			return;
		}

		// Кнопка гасит щелчок, и список выделения не меняет: кнопки под списком остались
		// бы на прежнем устройстве, а открытым было бы это.
		_filling = true;
		Rows.SelectedItem = row;
		_filling = false;

		Expand(row.Expanded ? null : row);
	}

	private void OnFlowChecked(object sender, RoutedEventArgs e)
	{
		// Разметка отмечает вкладку до того, как окно собрано целиком.
		if (!IsLoaded || sender is not RadioButton { Tag: string tag })
		{
			return;
		}

		_flow = tag == "input" ? EDataFlow.Capture : EDataFlow.Render;

		// Направления показывают разные наборы устройств: строки прошлого здесь не годятся.
		Close();
		_items.Clear();
		_byId.Clear();
		Refill();
		FlowChanged?.Invoke(_flow);
	}

	private static DeviceRow? Of(object sender) => (sender as FrameworkElement)?.DataContext as DeviceRow;

	private void OnMakeMain(object sender, RoutedEventArgs e)
	{
		if (Of(sender) is not { Device: var device })
		{
			return;
		}

		// Сначала правило, потом переключение: иначе пересчёт, который идёт следом за сменой
		// устройства, увёл бы звук обратно — устройства-то в приоритетах ещё нет.
		Apply(_switcher!.Rules.For(_flow).Promote(device));
		Endpoints.MakeCurrent(device);
	}

	// Команда уходит мгновенно, а связь поднимается ещё секунду-другую: список обновится сам,
	// когда Core Audio сообщит о смене состояния.
	private async void OnLink(object sender, RoutedEventArgs e)
	{
		if (Of(sender) is not { Device: var device } || sender is not Button button)
		{
			return;
		}

		button.IsEnabled = false;
		var connect = device.State != DeviceState.Active;
		var done = await Task.Run(() => connect ? BluetoothAudio.Connect(device) : BluetoothAudio.Disconnect(device));
		button.IsEnabled = true;

		if (!done)
		{
			_switcher!.Log(connect ? "langLogConnectFailed" : "langLogDisconnectFailed", device.Name);
		}
	}

	private void OnJoin(object sender, RoutedEventArgs e) => Edit(sender, (config, device) => config.Prioritise(device));

	private void OnMakeBlocked(object sender, RoutedEventArgs e) => Edit(sender, (config, device) => config.Block(device));

	private void OnRaise(object sender, RoutedEventArgs e) => Edit(sender, (config, device) => config.Move(device, -1));

	private void OnLower(object sender, RoutedEventArgs e) => Edit(sender, (config, device) => config.Move(device, +1));

	// Правило — это подстрока имени, а не устройство: крестик на одной строке может снять
	// общий паттерн, которым живут и соседние устройства. Молча такое делать нельзя.
	private void OnDrop(object sender, RoutedEventArgs e)
	{
		if (Of(sender) is not { Device: var device })
		{
			return;
		}

		var config = _switcher!.Rules.For(_flow);
		var devices = Endpoints.All(_flow);
		var shared = config.Rules(device)
			.Where(pattern => devices.Count(other => Config.Matches(other, pattern)) > 1)
			.ToList();

		if (shared.Count > 0 && !Dialog.Ask(Window.GetWindow(this)!,
				Localization.Get("langClearRule"),
				Localization.Format("langClearShared", string.Join(", ", shared))))
		{
			return;
		}

		Apply(config.Clear(device));
	}

	private void Edit(object sender, Func<Config, AudioEndpoint, Config> change)
	{
		if (Of(sender) is { Device: var device })
		{
			Apply(change(_switcher!.Rules.For(_flow), device));
		}
	}

	// Правила второго направления при этом остаются как были: файл у них общий.
	private void Apply(Config config) => _switcher!.Save(_switcher.Rules.With(_flow, config));

	// Восстановление переустанавливает устройства и может перезапустить службу звука —
	// на потоке интерфейса окно замерло бы на все эти секунды.
	private async void OnRecover(object sender, RoutedEventArgs e)
	{
		RecoverButton.IsEnabled = false;
		await Task.Run(_switcher!.Recover);
		RecoverButton.IsEnabled = true;
	}

	/// <summary>
	/// Раскрыть строку, свернув прежнюю. Раскрытых всегда не больше одной: измеритель
	/// держит поток с микрофона, и держать их по числу строк незачем.
	/// </summary>
	private void Expand(DeviceRow? row)
	{
		if (ReferenceEquals(_open, row))
		{
			return;
		}

		Close();

		if (row is null)
		{
			return;
		}

		// Раскрывается и молчащее устройство: запретить HDMI-выход, которого сейчас нет, —
		// обычное дело, а раньше до него было не добраться.
		_open = row;
		Fill(row);
		row.Expanded = true;

		// Мерить можно только то, что идёт: пока с микрофона никто не пишет, потока нет,
		// и полоске нечего показывать. Windows в своей панели звука открывает его за тем же.
		if (row is { Capture: true, Active: true })
		{
			_signal = Endpoints.Signal(row.Device);
			_meter.Start();
		}
	}

	private void Close()
	{
		_meter.Stop();
		_signal?.Dispose();
		_signal = null;
		_until = default;

		if (_open is { } row)
		{
			row.Expanded = false;
			_open = null;
		}
	}

	private void Fill(DeviceRow row)
	{
		var device = row.Device;
		var capture = device.Flow == EDataFlow.Capture;
		var pinned = Volume.For(device);

		row.Capture = capture;
		row.LevelName = Localization.Get(capture ? "langSensitivity" : "langVolume");
		row.Volume = Endpoints.Level(device) ?? pinned?.Percent ?? 50;
		row.Hold = pinned is not null;
		row.Peak = 0;

		// Приговор относится к тому разу, когда его выносили: другое устройство — заново.
		row.Testing = false;
		row.Verdict = "";
		row.VerdictGood = null;

		// Правило ловит по куску имени и может накрыть соседей — об этом надо сказать.
		row.Shared = pinned is not null && pinned.Match != device.Name
			? Localization.Format("langLevelShared", pinned.Match)
			: null;

		row.Knobs = capture ? Read(device) : [];
		row.Bare = capture && row.Knobs.Count == 0;
	}

	private static List<KnobRow> Read(AudioEndpoint device) =>
	[
		.. Endpoints.Knobs(device).Select(knob => new KnobRow(
			Knob: knob,
			// Шаг ноль означает плавный ход: делений у такого ползунка нет.
			Step: knob.Step > 0 ? knob.Step : 1,
			Snap: knob.Step > 0)
		{
			Value = knob.Value,
			On = knob.On,
		})
	];

	private void ShowPeak()
	{
		if (_open is not { } row || _signal is not { } signal)
		{
			return;
		}

		// Ухо слышит в децибелах, а точка отдаёт долю от полной шкалы: речь идёт около
		// 0,1 — линейная полоска показала бы её как тишину. Шкала здесь от -60 dB.
		var peak = Math.Clamp(signal.Peak, 0, 1);
		var decibels = peak > 0 ? 20 * Math.Log10(peak) : _floor;

		row.Peak = Math.Clamp(1 - decibels / _floor, 0, 1) * 100;
		row.PeakText = decibels <= _floor
			? Localization.Get("langSignalSilent")
			: $"{(int)Math.Round(decibels)} dB";

		if (_until != default)
		{
			Listen(row, decibels);
		}
	}

	// Проверка слушает несколько секунд и запоминает самое громкое: по одному мгновению
	// не скажешь ничего — человек между словами молчит, и любой замер попал бы в паузу.
	private void Listen(DeviceRow row, double decibels)
	{
		_loudest = Math.Max(_loudest, decibels);

		if (DateTime.UtcNow < _until)
		{
			return;
		}

		_until = default;
		row.Testing = false;
		Judge(row, _loudest);
	}

	private void Judge(DeviceRow row, double loudest)
	{
		var level = (int)Math.Round(loudest);

		(row.Verdict, row.VerdictGood) = loudest switch
		{
			<= _nothing => (Localization.Get("langCheckNothing"), false),
			// Совет поднять усиление бесполезен там, где его нет: у такого устройства
			// остаётся только само расстояние до рта.
			< _quiet => (Localization.Format(row.Bare ? "langCheckQuietBare" : "langCheckQuiet", level), false),
			> _clipping => (Localization.Format("langCheckLoud", level), false),
			_ => (Localization.Format("langCheckGood", level), true),
		};

		// Сигнал может быть отличным, а писаться будет не отсюда: ровно этим и кончилась
		// первая попытка починить микрофон — говорили в гарнитуру, писался ноутбук.
		if (Endpoints.Current(EDataFlow.Capture)?.Id != row.Device.Id)
		{
			row.Verdict += " " + Localization.Get("langCheckNotDefault");
			row.VerdictGood = false;
		}
	}

	private void OnCheck(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not DeviceRow row || _signal is null)
		{
			return;
		}

		_loudest = _floor;
		_until = DateTime.UtcNow + _listen;
		row.Testing = true;
		row.VerdictGood = null;
		row.Verdict = Localization.Get("langCheckSpeak");
	}

	/// <summary>
	/// Привязать заряд из эфира к этому устройству. Объявления не подписаны именем, а адрес
	/// в них меняется по таймеру, поэтому связать их с гарнитурой может только человек:
	/// он один знает, что она сейчас на голове, а не у соседа за стеной.
	/// </summary>
	private async void OnBind(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not DeviceRow row
			|| Battery.Mac(row.Device.Node) is not { } mac)
		{
			return;
		}

		row.Pairing = true;
		row.BindNote = Localization.Get("langBindListening");

		var found = await Task.Run(() => Beacon.Sweep(_bindSeconds));

		row.Pairing = false;

		if (found.Count == 0)
		{
			row.BindNote = Localization.Get("langBindNothing");

			return;
		}

		// Самый громкий — тот, что ближе всех. Сила сигнала решает здесь и только здесь:
		// один раз, в секунду, когда человек сам сказал «это моё». Дальше устройство
		// узнают по коду модели, и подходить к нему для этого не надо.
		var best = found[0];
		Store.Current.BeaconBound[best.Key] = mac;
		Store.Save();

		row.BindNote = found.Count > 1
			? Localization.Format("langBindManyFound", best.Name, best.Charge.Text, found.Count - 1)
			: Localization.Format("langBindDone", best.Name, best.Charge.Text);

		Refill();
	}

	// Громкость ставится сразу, на каждом шаге ползунка: настраивают её на слух, а не по числу.
	private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if ((sender as FrameworkElement)?.DataContext is DeviceRow row && row.Expanded)
		{
			row.Volume = e.NewValue;
			Endpoints.SetLevel(row.Device, (int)e.NewValue);
		}
	}

	// Уровень уже стоит на устройстве, и Windows помнит его сама. Правило существует ровно
	// тогда, когда нажата эта галочка: другого смысла у него нет.
	private void OnHoldChanged(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox box || box.DataContext is not DeviceRow row || !row.Expanded)
		{
			return;
		}

		// Строку пересобирают и прокрутка, и перестановка — привязка нажмёт галочку заново.
		// Правило от этого рождаться не должно, да и Refill отсюда ушёл бы внутрь разметки.
		if ((box.IsChecked == true) == (Volume.For(row.Device) is not null))
		{
			return;
		}

		if (box.IsChecked == true)
		{
			Volume.Pin(row.Device, (int)row.Volume);
		}
		else
		{
			Volume.Unpin(row.Device);
		}

		Refill();
	}

	// Усиление уходит прямо в устройство, и Windows помнит его сама — правило тут не нужно.
	private void OnKnobChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		// Ручки рождаются уже со своими значениями: первый ход ползунка — это не правка.
		if ((sender as FrameworkElement)?.DataContext is not KnobRow row
			|| Math.Abs(e.NewValue - row.Value) < 0.001)
		{
			return;
		}

		row.Value = e.NewValue;
		Endpoints.Turn(row.Knob, (float)e.NewValue);
	}

	private void OnKnobToggled(object sender, RoutedEventArgs e)
	{
		if (sender is not CheckBox { DataContext: KnobRow row } box || (box.IsChecked == true) == row.On)
		{
			return;
		}

		row.On = box.IsChecked == true;
		Endpoints.Switch(row.Knob, row.On);
	}

	/// <summary>Окно уходит в трей — поток с микрофона надо отпустить.</summary>
	internal void Collapse() => Close();
}
