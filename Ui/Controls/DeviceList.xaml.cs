using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AudioDirigent;

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
	private AudioEndpoint? _level;
	private bool _levelOpening;
	private bool _levelMoved;

	public DeviceList()
	{
		InitializeComponent();
		Rows.ItemsSource = _rows;
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

		Sync(devices);

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

	private void OnSelected(object sender, SelectionChangedEventArgs e) => UpdateActions();

	private void OnFlowChecked(object sender, RoutedEventArgs e)
	{
		// Разметка отмечает вкладку до того, как окно собрано целиком.
		if (!IsLoaded || sender is not RadioButton { Tag: string tag })
		{
			return;
		}

		_flow = tag == "input" ? EDataFlow.Capture : EDataFlow.Render;

		// Направления показывают разные наборы устройств: строки прошлого здесь не годятся.
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

	// Уровень правится там же, где виден: плашка в строке открывает ползунок над собой.
	private void OnLevelChip(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { DataContext: DeviceRow row } chip)
		{
			return;
		}

		_level = row.Device;
		_levelMoved = false;
		LevelDevice.Text = row.Name;

		var pinned = Volume.For(row.Device);
		LevelShared.Text = Localization.Format("langLevelShared", pinned?.Match ?? "");
		LevelShared.Visibility = pinned is not null && pinned.Match != row.Device.Name
			? Visibility.Visible
			: Visibility.Collapsed;

		// Ползунок встаёт на закреплённый уровень, а если его нет — на нынешний уровень
		// устройства: иначе первое же движение швырнуло бы громкость от нуля.
		_levelOpening = true;
		LevelSlider.Value = pinned?.Percent ?? Audio.GetVolume(row.Device.Id) ?? 50;
		_levelOpening = false;

		LevelMenu.PlacementTarget = chip;
		LevelMenu.IsOpen = true;
	}

	// Громкость ставится сразу, на каждом шаге ползунка: настраивают её на слух, а не по числу.
	private void OnLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		LevelValue.Text = $"{(int)e.NewValue}%";

		if (_levelOpening || _level is not { } device)
		{
			return;
		}

		_levelMoved = true;
		Audio.SetVolume(device.Id, (int)e.NewValue);
	}

	// В файл уровень уходит один раз, при закрытии: писать его на каждый шаг ползунка незачем.
	// И только если ползунок двигали: заглянуть в чужой уровень — не значит закрепить его.
	private void OnLevelClosed(object sender, EventArgs e)
	{
		if (_levelMoved && _level is { } device)
		{
			Volume.Pin(device, (int)LevelSlider.Value);
			Refill();
		}

		_level = null;
	}

	private void OnLevelUnpin(object sender, RoutedEventArgs e)
	{
		if (_level is { } device)
		{
			Volume.Unpin(device);

			// Обнуляем до закрытия попапа, иначе он тут же запишет уровень обратно.
			_level = null;
			LevelMenu.IsOpen = false;
			Refill();
		}
	}

	/// <summary>Окно уходит в трей — ползунок закрываем сами, иначе уровень не запишется.</summary>
	internal void CloseLevel() => LevelMenu.IsOpen = false;
}
