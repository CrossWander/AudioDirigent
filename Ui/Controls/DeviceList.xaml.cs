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
internal sealed record KnobRow(string Device, MicrophoneKnob Knob, string Name, bool IsLevel,
	double Minimum, double Maximum, double Step, bool Snap, double Value, bool On);

/// <summary>
/// Список устройств одного направления и всё, что с ними делают. Строки живут дольше одного
/// обновления: список сверяется по номеру устройства и правит существующие строки вместо того,
/// чтобы собирать их заново, — иначе выделение слетало бы на каждое событие Core Audio.
/// </summary>
public partial class DeviceList : UserControl
{
	private readonly ObservableCollection<DeviceRow> _rows = [];

	private Switcher? _switcher;
	private EDataFlow _flow = EDataFlow.Render;
	private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(60) };

	private DeviceRow? _open;
	private Meter? _signal;
	private bool _filling;

	// Ниже этого уровня шкала уже ничего не различает — там комнатная тишина.
	private const double Floor = -60;

	public DeviceList()
	{
		InitializeComponent();
		Rows.ItemsSource = _rows;
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
		var current = Audio.GetDefault(_flow, ERole.Multimedia);
		var devices = Audio.ListDevices(_flow)
			.OrderByDescending(device => device.State == DeviceState.Active)
			.ThenBy(device => device.Name)
			.ToList();

		// Перебор строк меняет выделение, а на выделение подвешено раскрытие: пока идёт
		// сверка, оно не должно принимать перестановку за выбор человека.
		_filling = true;
		Sync(devices);
		_filling = false;

		// Раскрытое устройство могло и пропасть — тогда отпускаем и поток с него.
		if (_open is { } open && !_rows.Contains(open))
		{
			Close();
		}

		foreach (var row in _rows)
		{
			Describe(row, config, current);
		}

		// Перестановка строк уводит прокрутку: без выделения смотреть надо на живые
		// устройства, а они наверху.
		if (Rows.SelectedItem is null && _rows.Count > 0)
		{
			Rows.ScrollIntoView(_rows[0]);
		}

		UpdateActions();
	}

	/// <summary>Привести набор строк к набору устройств, сохранив те, что уже есть.</summary>
	private void Sync(List<AudioEndpoint> devices)
	{
		var byId = _rows.ToDictionary(row => row.Device.Id);

		for (var index = 0; index < devices.Count; index++)
		{
			var device = devices[index];
			if (byId.TryGetValue(device.Id, out var row))
			{
				row.Update(device);
				var at = _rows.IndexOf(row);
				if (at != index)
				{
					_rows.Move(at, index);
				}
			}
			else
			{
				_rows.Insert(index, new DeviceRow(device));
			}
		}

		var live = devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
		for (var index = _rows.Count - 1; index >= 0; index--)
		{
			if (!live.Contains(_rows[index].Device.Id))
			{
				_rows.RemoveAt(index);
			}
		}
	}

	private void Describe(DeviceRow row, Config config, AudioEndpoint? current)
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

		row.Active = active;
		row.Current = device.Id == current?.Id;
		row.State = Describe(device.State);
		row.Mark = blocked ? RowMark.Blocked : rank >= 0 ? RowMark.Priority : undecided ? RowMark.Undecided : RowMark.None;
		row.Badge = blocked ? "✕" : rank >= 0 ? (rank + 1).ToString() : undecided ? "+" : "";
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

	private AudioEndpoint? Selected() => (Rows.SelectedItem as DeviceRow)?.Device;

	/// <summary>
	/// Две кнопки, которые зависят не от правил, а от самого устройства. «Сделать основным» —
	/// то же предложение, что и в карточке над треем: она живёт четыре секунды, пропустить её
	/// нормально, и решение должно оставаться под рукой. Связь поднимается и рвётся только у
	/// Bluetooth: у остального за это отвечает разъём.
	/// </summary>
	private void UpdateActions()
	{
		if (_switcher is not { } switcher)
		{
			return;
		}

		var device = Selected();
		var config = switcher.Rules.For(_flow);

		var undecided = device is { State: DeviceState.Active }
			&& config.Rank(device) < 0
			&& !config.Blocks(device)
			&& device.Id != Audio.GetDefault(_flow, ERole.Multimedia)?.Id;

		MakeMainButton.Visibility = undecided ? Visibility.Visible : Visibility.Collapsed;

		var linkable = device is { Bluetooth: true, State: DeviceState.Active or DeviceState.Unplugged };
		LinkButton.Visibility = linkable ? Visibility.Visible : Visibility.Collapsed;
		LinkButton.Content = Localization.Get(
			device?.State == DeviceState.Active ? "langDisconnect" : "langConnect");
	}

	private void OnSelected(object sender, SelectionChangedEventArgs e)
	{
		if (!_filling)
		{
			Expand(Rows.SelectedItem as DeviceRow);
		}

		UpdateActions();
	}

	// Шеврон делает то же самое: он нужен, чтобы раскрытие было видно, а не угадывалось.
	private void OnChevron(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is DeviceRow row)
		{
			Expand(row.Expanded ? null : row);
		}
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
		_rows.Clear();
		Refill();
		FlowChanged?.Invoke(_flow);
	}

	private void OnMakeMain(object sender, RoutedEventArgs e)
	{
		if (Selected() is not { } device)
		{
			return;
		}

		// Сначала правило, потом переключение: иначе пересчёт, который идёт следом за сменой
		// устройства, увёл бы звук обратно — устройства-то в приоритетах ещё нет.
		Apply(_switcher!.Rules.For(_flow).Promote(device));
		Audio.SetDefault(device.Id);
	}

	// Команда уходит мгновенно, а связь поднимается ещё секунду-другую: список обновится сам,
	// когда Core Audio сообщит о смене состояния.
	private async void OnLink(object sender, RoutedEventArgs e)
	{
		if (Selected() is not { } device)
		{
			return;
		}

		LinkButton.IsEnabled = false;
		var connect = device.State != DeviceState.Active;
		var done = await Task.Run(() => connect ? BluetoothAudio.Connect(device) : BluetoothAudio.Disconnect(device));
		LinkButton.IsEnabled = true;

		if (!done)
		{
			_switcher!.Log(connect ? "langLogConnectFailed" : "langLogDisconnectFailed", device.Name);
		}
	}

	private void OnMakePriority(object sender, RoutedEventArgs e) => Edit((config, device) => config.Prioritise(device));

	private void OnMakeBlocked(object sender, RoutedEventArgs e) => Edit((config, device) => config.Block(device));

	// Правило — это подстрока имени, а не устройство: «Сбросить» на одной строке может снять
	// общий паттерн, которым живут и соседние устройства. Молча такое делать нельзя.
	private void OnClearRule(object sender, RoutedEventArgs e)
	{
		if (Selected() is not { } device)
		{
			return;
		}

		var config = _switcher!.Rules.For(_flow);
		var devices = Audio.ListDevices(_flow);
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

	private void OnMoveUp(object sender, RoutedEventArgs e) => Edit((config, device) => config.Move(device, -1));

	private void OnMoveDown(object sender, RoutedEventArgs e) => Edit((config, device) => config.Move(device, +1));

	private void Edit(Func<Config, AudioEndpoint, Config> change)
	{
		if (Selected() is { } device)
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

		if (row is not { Active: true })
		{
			return;
		}

		_open = row;
		Fill(row);
		row.Expanded = true;

		// Полоска меряет по своему потоку: без него пик у точки всегда ноль, сколько
		// в микрофон ни говори. Windows в своей панели звука открывает его ровно за этим.
		if (row.Capture)
		{
			_signal = Meter.Open(row.Device.Id);
			_meter.Start();
		}
	}

	private void Close()
	{
		_meter.Stop();
		_signal?.Dispose();
		_signal = null;

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
		row.Volume = Audio.GetVolume(device.Id) ?? pinned?.Percent ?? 50;
		row.Hold = pinned is not null;
		row.Peak = 0;

		// Правило ловит по куску имени и может накрыть соседей — об этом надо сказать.
		row.Shared = pinned is not null && pinned.Match != device.Name
			? Localization.Format("langLevelShared", pinned.Match)
			: null;

		row.Knobs = capture ? Read(device) : [];
		row.Bare = capture && row.Knobs.Count == 0;
	}

	private static List<KnobRow> Read(AudioEndpoint device) =>
	[
		.. Microphone.Knobs(device.Id).Select(knob => new KnobRow(
			Device: device.Id,
			Knob: knob,
			Name: knob.Name,
			IsLevel: knob.Kind == MicrophoneKnobKind.Level,
			Minimum: knob.Minimum,
			Maximum: knob.Maximum,
			// Шаг ноль означает плавный ход: делений у такого ползунка нет.
			Step: knob.Step > 0 ? knob.Step : 1,
			Snap: knob.Step > 0,
			Value: knob.Value,
			On: knob.On))
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
		var decibels = peak > 0 ? 20 * Math.Log10(peak) : Floor;

		row.Peak = Math.Clamp(1 - decibels / Floor, 0, 1) * 100;
		row.PeakText = decibels <= Floor ? Localization.Get("langSignalSilent") : $"{decibels:0} dB";
	}

	// Громкость ставится сразу, на каждом шаге ползунка: настраивают её на слух, а не по числу.
	private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if ((sender as FrameworkElement)?.DataContext is DeviceRow row && row.Expanded)
		{
			Audio.SetVolume(row.Device.Id, (int)e.NewValue);
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
		if (_open is { Expanded: true } && (sender as FrameworkElement)?.DataContext is KnobRow row)
		{
			Microphone.SetLevel(row.Device, row.Knob.Part, (float)e.NewValue);
		}
	}

	private void OnKnobToggled(object sender, RoutedEventArgs e)
	{
		if (_open is { Expanded: true } && sender is CheckBox { DataContext: KnobRow row } box)
		{
			Microphone.SetAutoGain(row.Device, row.Knob.Part, box.IsChecked == true);
		}
	}

	/// <summary>Окно уходит в трей — поток с микрофона надо отпустить.</summary>
	internal void CloseLevel() => Close();
}
