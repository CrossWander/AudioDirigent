using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioDirigent;

/// <summary>
/// Окно ничего не делает само — оно расставляет части и сводит их друг с другом. Всё, что
/// показывает и правит одну вещь, живёт в своём контроле; сюда попадает только связь между ними.
/// </summary>
public partial class MainWindow : Window
{
	private readonly Switcher _switcher;
	private readonly Action _togglePause;

	internal MainWindow(Switcher switcher, Action togglePause, Action<bool> setHotkeys)
	{
		_switcher = switcher;
		_togglePause = togglePause;

		InitializeComponent();

		Current.Attach(switcher);
		Devices.Attach(switcher);
		Settings.Attach(switcher, setHotkeys);

		Bar.Settings += OnSettings;
		Bar.Minimise += () => WindowState = WindowState.Minimized;
		Bar.Maximise += () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		Bar.Close += Close;

		Current.PauseToggled += OnPauseToggled;
		Devices.FlowChanged += _ => Current.Refill(Devices.Flow);
		Settings.UpdateChecked += Footer.Show;
		Log.HeightWanted += Grow;

		SettingsMenu.CustomPopupPlacementCallback = PlaceUnder;

		_switcher.Logged += OnLogged;
		_switcher.DevicesChanged += OnDevicesChanged;
		Localization.Changed += OnLanguageChanged;
		Theme.Changed += OnThemeChanged;

		Refill();
	}

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

	/// <summary>Паузу ставят и из трея, и сочетанием клавиш — значок состояния должен догнать.</summary>
	public void SyncPauseState() => Dispatcher.BeginInvoke(Current.RefreshBadge);

	/// <summary>Итог проверки обновлений, сделанной без окна: показать его, когда оно открылось.</summary>
	internal void ShowUpdate(Release? release) => Dispatcher.BeginInvoke(() =>
	{
		Footer.Show(release);
		if (release is not null)
		{
			Settings.Show(release);
		}
	});

	internal void Detach()
	{
		_switcher.Logged -= OnLogged;
		_switcher.DevicesChanged -= OnDevicesChanged;
		Localization.Changed -= OnLanguageChanged;
		Theme.Changed -= OnThemeChanged;
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		((HwndSource)PresentationSource.FromVisual(this)!).AddHook(Monitors.LimitMaximizedSize);
	}

	/// <summary>Крестик прячет окно в трей — программа продолжает работать.</summary>
	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		ToTray();
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

	// Кнопка настроек стоит у правого края заголовка, а панель шире её: равняем по правому
	// краю, иначе она вылезала бы за окно вправо.
	private static CustomPopupPlacement[] PlaceUnder(Size menu, Size button, Point offset) =>
		[new CustomPopupPlacement(new Point(button.Width - menu.Width, button.Height - 8), PopupPrimaryAxis.Horizontal)];

	private void OnSettings(FrameworkElement button)
	{
		// Настройки правят и мимо панели — например, паузу из трея.
		Settings.Reload();
		SettingsMenu.PlacementTarget = button;
		SettingsMenu.IsOpen = true;
	}

	private void OnPauseToggled()
	{
		_togglePause();
		Current.RefreshBadge();
	}

	private void OnLogged(LogEntry entry) => Dispatcher.BeginInvoke(() => Log.Add(entry));

	private void OnDevicesChanged() => Dispatcher.BeginInvoke(Refill);

	private void OnLanguageChanged()
	{
		Footer.SelectLanguage();
		Log.Render();
		Settings.Reload();
		Refill();
	}

	// Часть подписей окрашена из кода — по состоянию, а не по роли: их надо перечитать.
	private void OnThemeChanged() => Refill();

	private void Refill()
	{
		Current.Refill(Devices.Flow);
		Devices.Refill();
	}

	// Раскрытый журнал не должен съедать список устройств — окно подрастает ровно на его
	// высоту. Меняем её в том же проходе разметки, что и содержимое: пока она ехала
	// анимацией, список успевал подскочить и осесть обратно, и раскрытие выглядело рывком.
	private void Grow(double amount)
	{
		if (WindowState != WindowState.Maximized)
		{
			Height += amount;
		}
	}

	// Ползунок уровня записывает громкость в момент закрытия, а вместе с окном он просто
	// пропадает с экрана, не закрываясь. Закрываем его руками, иначе уровень потеряется.
	private void ToTray()
	{
		Devices.CloseLevel();
		SettingsMenu.IsOpen = false;
		Hide();
	}
}
