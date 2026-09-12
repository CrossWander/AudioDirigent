using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioDirigent;

public partial class MainWindow : Window
{
	private readonly Switcher _switcher;
	private readonly Action _togglePause;
	private readonly Action<bool> _setHotkeys;

	// Журнал держим записями, а не готовым текстом: иначе при смене языка
	// строки остались бы на том, при котором произошли.
	private readonly List<LogEntry> _log = [];
	private EDataFlow _flow = EDataFlow.Render;
	private bool _switchingLanguage;
	private bool _levelOpening;
	private bool _levelMoved;
	private AudioEndpoint? _level;
	private (UpdateState State, string? Version)? _update;

	internal MainWindow(Switcher switcher, Action togglePause, Action<bool> setHotkeys)
	{
		_switcher = switcher;
		_togglePause = togglePause;
		_setHotkeys = setHotkeys;

		InitializeComponent();

		Credits.Text = $"Developed by Dykalo Pavlo, 2026   ·   {Build.Version}";
		AutostartToggle.IsChecked = Autostart.IsEnabled();
		UsbPowerToggle.IsChecked = UsbPower.Enabled;
		PauseToggle.IsChecked = switcher.Paused;
		NotifyToggle.IsChecked = Store.Current.Notify;
		PopupToggle.IsChecked = Store.Current.Popup;
		HotkeyToggle.IsChecked = Store.Current.Hotkeys;
		HotkeyHint.Text = Localization.Format("langHotkeysHint", Store.Current.PauseHotkey, Store.Current.RecoverHotkey);
		_log.AddRange(Journal.Recent(40));
		RenderLog();
		SelectLanguageButton();

		SettingsMenu.CustomPopupPlacementCallback = PlaceUnder;

		_switcher.Logged += OnLogged;
		_switcher.DevicesChanged += OnDevicesChanged;
		Localization.Changed += OnLanguageChanged;

		Refill();
	}

	/// <summary>Строка списка устройств — только то, что показывает шаблон.</summary>
	internal sealed record DeviceRow(
		AudioEndpoint Device,
		string Name,
		string State,
		string Badge,
		string? BadgeHint,
		string Note,
		string Level,
		string LevelHint,
		Brush LevelBackground,
		Brush LevelForeground,
		Brush BadgeBackground,
		Brush BadgeForeground,
		Brush NameBrush,
		Brush StateBrush,
		FontWeight NameWeight,
		Visibility NoteVisibility);

	/// <summary>Плавное появление — окно показывают из трея, а не создают заново.</summary>
	public void AnimateIn()
	{
		BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
		Shell.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
			new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			});
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		((HwndSource)PresentationSource.FromVisual(this)!).AddHook(Monitors.LimitMaximizedSize);
	}

	private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

	private void OnMaximize(object sender, RoutedEventArgs e) =>
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	private void OnCloseToTray(object sender, RoutedEventArgs e) => Close();

	private void OnLanguageChecked(object sender, RoutedEventArgs e)
	{
		if (_switchingLanguage || sender is not RadioButton { Tag: string code } || code == Localization.Current)
		{
			return;
		}

		Localization.Apply(code);
	}

	private void OnLanguageChanged()
	{
		SelectLanguageButton();
		RenderLog();
		RenderUpdate();
		Refill();
	}

	private void SelectLanguageButton()
	{
		_switchingLanguage = true;
		LangRu.IsChecked = Localization.Current == "ru";
		LangUk.IsChecked = Localization.Current == "uk";
		LangEn.IsChecked = Localization.Current == "en";
		_switchingLanguage = false;
	}

	private void OnLogged(LogEntry entry) => Dispatcher.BeginInvoke(() =>
	{
		_log.Add(entry);
		if (_log.Count > 200)
		{
			_log.RemoveRange(0, _log.Count - 200);
		}

		RenderLog();
	});

	private void RenderLog()
	{
		Log.Text = string.Join(Environment.NewLine, _log.Select(entry => entry.Render()));
		LogScroll.ScrollToEnd();
	}

	private void OnDevicesChanged() => Dispatcher.BeginInvoke(Refill);

	public void SyncPauseState() => Dispatcher.BeginInvoke(() =>
	{
		PauseToggle.IsChecked = _switcher.Paused;
		UpdateStateBadge();
	});

	// Список показывает одно направление за раз: правила у них разные, а таблица одна.
	private void OnFlowChecked(object sender, RoutedEventArgs e)
	{
		// Разметка отмечает вкладку до того, как окно собрано целиком.
		if (IsLoaded && sender is RadioButton { Tag: string tag })
		{
			_flow = tag == "input" ? EDataFlow.Capture : EDataFlow.Render;
			Refill();
		}
	}

	private void Refill()
	{
		var config = _switcher.Rules.For(_flow);
		var devices = Audio.ListDevices(_flow);
		var current = Audio.GetDefault(_flow, ERole.Multimedia);

		CurrentDevice.Text = current?.Name ?? Localization.Get("langNone");
		DeviceCount.Text = Localization.Format("langActiveOf", devices.Count(d => d.State == DeviceState.Active), devices.Count);

		(ProbeStatus.Text, var probeColour) = (_switcher.HasProbe, _switcher.DeviceOn) switch
		{
			(false, _) => (Localization.Get("langProbeNotFound"), "Muted"),
			(true, true) => (Localization.Get("langProbeOn"), "Good"),
			(true, false) => (Localization.Get("langProbeOff"), "Accent"),
			(true, null) => (Localization.Get("langProbeSilent"), "Muted"),
		};
		ProbeDot.Fill = Paint(probeColour);

		var selectedId = Selected()?.Id;

		Devices.ItemsSource = devices
			.OrderByDescending(d => d.State == DeviceState.Active)
			.ThenBy(d => d.Name)
			.Select(device => Row(device, config, current))
			.ToList();

		Devices.SelectedItem = Devices.Items.Cast<DeviceRow>().FirstOrDefault(row => row.Device.Id == selectedId);
		if (Devices.SelectedItem is null && Devices.Items.Count > 0)
		{
			Devices.ScrollIntoView(Devices.Items[0]);
		}

		UpdateStateBadge();
	}

	private DeviceRow Row(AudioEndpoint device, Config config, AudioEndpoint? current)
	{
		var rank = config.Rank(device);
		var blocked = config.Blocks(device);
		var active = device.State == DeviceState.Active;
		var isCurrent = device.Id == current?.Id;
		var level = Volume.For(device);

		// Значок дают правила, а правило — это фрагмент имени. Пока его не назвать,
		// непонятно, почему у устройства номер и что именно снимет «Сбросить».
		var rule = blocked
			? config.Blocked.First(pattern => Config.Matches(device, pattern))
			: rank >= 0 ? config.Priority[rank] : "";

		return new DeviceRow(
			Device: device,
			Name: device.Name,
			State: Describe(device.State),
			Badge: blocked ? "✕" : rank >= 0 ? (rank + 1).ToString() : "",
			// Значок сам по себе ничего не объясняет; null у пустого — подсказки просто нет.
			BadgeHint: blocked ? Localization.Format("langBadgeBlocked", rule)
				: rank >= 0 ? Localization.Format("langBadgePriority", rank + 1, rule) : null,
			Note: isCurrent ? Localization.Get("langCurrentDevice") : "",
			// Прочерк вместо пустоты: плашка — единственный способ задать уровень, и она
			// должна быть видна и там, где закреплять ещё нечего.
			Level: level is null ? "—" : $"{level.Percent}%",
			LevelHint: level is null
				? Localization.Get("langLevelNone")
				: Localization.Format("langLevelPinned", level.Percent, level.Match),
			LevelBackground: level is null ? Brushes.Transparent : Paint("Badge"),
			LevelForeground: Paint(level is null ? "Muted" : "TextDim"),
			BadgeBackground: blocked ? Paint("Accent") : rank >= 0 ? Paint("Badge") : Brushes.Transparent,
			BadgeForeground: Paint(blocked ? "BadgeText" : "TextDim"),
			NameBrush: Paint(active ? "Text" : "TextDim"),
			StateBrush: Paint(active ? "Good" : "Muted"),
			NameWeight: isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
			NoteVisibility: isCurrent ? Visibility.Visible : Visibility.Collapsed);
	}

	private void UpdateStateBadge()
	{
		var paused = _switcher.Paused;
		StateText.Text = Localization.Get(paused ? "langPausedBadge" : "langWatching");
		StateDot.Fill = Paint(paused ? "Muted" : "Good");
		StateBadge.Background = Paint(paused ? "Badge" : "WatchingFill");
		StateBadge.BorderBrush = Paint(paused ? "PausedLine" : "WatchingLine");
	}

	private static string Describe(DeviceState state) => Localization.Get(state switch
	{
		DeviceState.Active => "langStateActive",
		DeviceState.Disabled => "langStateDisabled",
		DeviceState.NotPresent => "langStateNotPresent",
		DeviceState.Unplugged => "langStateUnplugged",
		_ => state.ToString(),
	});

	private AudioEndpoint? Selected() => (Devices.SelectedItem as DeviceRow)?.Device;

	// Палитра живёт в словаре окна: вторая копия в коде разъезжалась с разметкой.
	private Brush Paint(string key) => (Brush)FindResource(key);

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

		var config = _switcher.Rules.For(_flow);
		var devices = Audio.ListDevices(_flow);
		var shared = config.Rules(device)
			.Where(pattern => devices.Count(other => Config.Matches(other, pattern)) > 1)
			.ToList();

		if (shared.Count > 0 && MessageBox.Show(this,
				Localization.Format("langClearShared", string.Join(", ", shared)),
				Localization.Get("langClearRule"),
				MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
		{
			return;
		}

		Apply(config.Clear(device));
	}

	private void OnMoveUp(object sender, RoutedEventArgs e) => Edit((config, device) => config.Move(device, -1));

	private void OnMoveDown(object sender, RoutedEventArgs e) => Edit((config, device) => config.Move(device, +1));

	/// <summary>Изменить правила для выделенного устройства; сохранение сразу их применяет и обновляет список.</summary>
	private void Edit(Func<Config, AudioEndpoint, Config> change)
	{
		if (Selected() is { } device)
		{
			Apply(change(_switcher.Rules.For(_flow), device));
		}
	}

	// Правила второго направления при этом остаются как были: файл у них общий.
	private void Apply(Config config) => _switcher.Save(_switcher.Rules.With(_flow, config));

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

	private void OnSettingsMenu(object sender, RoutedEventArgs e) => SettingsMenu.IsOpen = true;

	// Кнопка настроек стоит у правого края заголовка, а список шире её: равняем по правому
	// краю, иначе он вылезал бы за окно вправо.
	private static CustomPopupPlacement[] PlaceUnder(Size menu, Size button, Point offset) =>
		[new CustomPopupPlacement(new Point(button.Width - menu.Width, button.Height + 6), PopupPrimaryAxis.Horizontal)];

	// Восстановление переустанавливает устройства и может перезапустить службу звука —
	// на потоке интерфейса окно замерло бы на все эти секунды.
	private async void OnRecover(object sender, RoutedEventArgs e)
	{
		RecoverButton.IsEnabled = false;
		await Task.Run(_switcher.Recover);
		RecoverButton.IsEnabled = true;
	}

	private void OnLogExpanded(object sender, RoutedEventArgs e) => ResizeForLog(+1);

	private void OnLogCollapsed(object sender, RoutedEventArgs e) => ResizeForLog(-1);

	// Раскрытый журнал не должен съедать список устройств — отдаём ему высоту окна, ровно
	// столько, сколько занимает его панель. Высота меняется в том же проходе разметки, что и
	// само содержимое: пока она ехала анимацией, список успевал подскочить на высоту журнала
	// и осесть обратно, и раскрытие выглядело рывком.
	private void ResizeForLog(int direction)
	{
		if (WindowState != WindowState.Maximized)
		{
			Height += direction * (LogPanel.Height + LogPanel.Margin.Top + LogPanel.Margin.Bottom);
		}
	}

	private void OnPauseChanged(object sender, RoutedEventArgs e)
	{
		if (PauseToggle.IsChecked != _switcher.Paused)
		{
			_togglePause();
		}

		UpdateStateBadge();
	}

	private void OnAutostartChanged(object sender, RoutedEventArgs e)
	{
		var wanted = AutostartToggle.IsChecked == true;
		if (wanted == Autostart.IsEnabled())
		{
			return;
		}

		if (Autostart.Set(wanted) is { } error)
		{
			MessageBox.Show(this, error, Localization.Get("langSchedulerTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
			AutostartToggle.IsChecked = Autostart.IsEnabled();
			return;
		}

		_switcher.Log(wanted ? "langLogAutostartOn" : "langLogAutostartOff", Autostart.TaskName);
	}

	// Правка галок питания идёт через WMI и занимает секунды — на потоке интерфейса
	// окно бы замерло, как и на восстановлении.
	private async void OnUsbPowerChanged(object sender, RoutedEventArgs e)
	{
		var wanted = UsbPowerToggle.IsChecked == true;
		if (wanted == UsbPower.Enabled)
		{
			return;
		}

		UsbPowerToggle.IsEnabled = false;
		var error = await Task.Run(() => UsbPower.Set(wanted, _switcher.Log));
		UsbPowerToggle.IsEnabled = true;

		if (error is not null)
		{
			MessageBox.Show(this, error, Localization.Get("langUsbPower"), MessageBoxButton.OK, MessageBoxImage.Warning);
		}

		UsbPowerToggle.IsChecked = UsbPower.Enabled;
	}

	private void OnNotifyChanged(object sender, RoutedEventArgs e)
	{
		Store.Current.Notify = NotifyToggle.IsChecked == true;
		Store.Save();
	}

	private void OnPopupChanged(object sender, RoutedEventArgs e)
	{
		Store.Current.Popup = PopupToggle.IsChecked == true;
		Store.Save();
	}

	private void OnHotkeysChanged(object sender, RoutedEventArgs e)
	{
		Store.Current.Hotkeys = HotkeyToggle.IsChecked == true;
		Store.Save();
		_setHotkeys(Store.Current.Hotkeys);
	}

	// Единственный запрос в сеть за всё время работы, и только по этой кнопке.
	private async void OnCheckUpdates(object sender, RoutedEventArgs e)
	{
		UpdateButton.IsEnabled = false;
		_update = null;
		RenderUpdate();
		UpdateResult.Text = Localization.Get("langUpdateChecking");

		_update = await Updates.Check();
		RenderUpdate();
		UpdateButton.IsEnabled = true;

		_switcher.Log(_update.Value.State switch
		{
			UpdateState.Newer => "langLogUpdateFound",
			UpdateState.Latest => "langLogUpdateNone",
			_ => "langLogUpdateFailed",
		}, _update.Value.Version ?? Build.Version);
	}

	// Итог проверки виден в двух местах: у кнопки и ссылкой в подвале — попап закроется,
	// а напоминание о новой версии должно остаться на глазах.
	private void RenderUpdate()
	{
		UpdateLink.Visibility = Visibility.Collapsed;
		if (_update is not { } result)
		{
			UpdateResult.Text = "";

			return;
		}

		var newer = result.State == UpdateState.Newer;
		UpdateResult.Text = result.State switch
		{
			UpdateState.Newer => Localization.Format("langUpdateAvailable", result.Version!),
			UpdateState.Latest => Localization.Format("langUpdateLatest", Build.Version),
			_ => Localization.Get("langUpdateFailed"),
		};

		UpdateResult.Foreground = Paint(newer ? "Good" : "TextDim");
		UpdateResult.Cursor = newer ? Cursors.Hand : Cursors.Arrow;

		if (newer)
		{
			UpdateLink.Text = UpdateResult.Text;
			UpdateLink.Visibility = Visibility.Visible;
		}
	}

	private void OnOpenReleases(object sender, MouseButtonEventArgs e)
	{
		if (_update?.State == UpdateState.Newer)
		{
			Process.Start(new ProcessStartInfo(Updates.Releases) { UseShellExecute = true });
		}
	}

	// Крестик прячет окно в трей, приложение продолжает работать.
	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		ToTray();
	}

	// Попап уровня записывает громкость в момент закрытия, а вместе с окном он просто
	// пропадает с экрана, не закрываясь. Закрываем его руками, иначе уровень потеряется.
	private void ToTray()
	{
		LevelMenu.IsOpen = false;
		Hide();
	}

	protected override void OnStateChanged(EventArgs e)
	{
		base.OnStateChanged(e);

		// Сворачивание уводит в трей, а не на панель задач.
		if (WindowState == WindowState.Minimized)
		{
			ToTray();
			WindowState = WindowState.Normal;
		}
	}

	internal void Detach()
	{
		_switcher.Logged -= OnLogged;
		_switcher.DevicesChanged -= OnDevicesChanged;
		Localization.Changed -= OnLanguageChanged;
	}
}
