using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AudioDirigent;

/// <summary>Всё, что ставят один раз и забывают. Плюс проверка новой версии — не настройка, а действие.</summary>
public partial class SettingsPanel : UserControl
{
	private Switcher? _switcher;
	private Action<bool>? _setHotkeys;
	private Release? _release;
	private bool _loading;

	public SettingsPanel() => InitializeComponent();

	/// <summary>Итог проверки виден и в подвале окна: попап закроется, а новость должна остаться.</summary>
	internal event Action<Release?>? UpdateChecked;

	internal void Attach(Switcher switcher, Action<bool> setHotkeys)
	{
		_switcher = switcher;
		_setHotkeys = setHotkeys;
		Reload();
	}

	/// <summary>Перечитать состояние: значения могли поменяться мимо этой панели.</summary>
	internal void Reload()
	{
		// Пока расставляем галки, обработчики не должны принимать это за выбор человека.
		_loading = true;

		AutostartToggle.IsChecked = Autostart.IsEnabled();
		UsbPowerToggle.IsChecked = UsbPower.Enabled;
		PopupToggle.IsChecked = Store.Current.Popup;
		NotifyToggle.IsChecked = Store.Current.Notify;
		HotkeyToggle.IsChecked = Store.Current.Hotkeys;
		AutoUpdateToggle.IsChecked = Store.Current.AutoUpdate;
		HotkeyHint.Text = Localization.Format("langHotkeysHint",
			Store.Current.PauseHotkey, Store.Current.RecoverHotkey);

		ThemeAuto.IsChecked = Theme.Mode == ThemeMode.Auto;
		ThemeLight.IsChecked = Theme.Mode == ThemeMode.Light;
		ThemeDark.IsChecked = Theme.Mode == ThemeMode.Dark;

		_loading = false;
	}

	/// <summary>Показать итог проверки, сделанной не отсюда, — например, ежедневной.</summary>
	internal void Show(Release release)
	{
		_release = release;
		Render();
	}

	private void OnThemeChecked(object sender, RoutedEventArgs e)
	{
		if (!_loading && sender is RadioButton { Tag: string tag } && Enum.TryParse<ThemeMode>(tag, out var mode))
		{
			Theme.Apply(mode);
		}
	}

	private void OnAutostartChanged(object sender, RoutedEventArgs e)
	{
		var wanted = AutostartToggle.IsChecked == true;
		if (_loading || wanted == Autostart.IsEnabled())
		{
			return;
		}

		if (Autostart.Set(wanted) is { } error)
		{
			Warn(error, "langSchedulerTitle");
			AutostartToggle.IsChecked = Autostart.IsEnabled();

			return;
		}

		_switcher?.Log(wanted ? "langLogAutostartOn" : "langLogAutostartOff", Autostart.TaskName);
	}

	// Правка галок питания идёт через WMI и занимает секунды — на потоке интерфейса окно
	// бы замерло, как и на восстановлении.
	private async void OnUsbPowerChanged(object sender, RoutedEventArgs e)
	{
		var wanted = UsbPowerToggle.IsChecked == true;
		if (_loading || wanted == UsbPower.Enabled)
		{
			return;
		}

		UsbPowerToggle.IsEnabled = false;
		var error = await Task.Run(() => UsbPower.Set(wanted, _switcher!.Log));
		UsbPowerToggle.IsEnabled = true;

		if (error is not null)
		{
			Warn(error, "langUsbPower");
		}

		UsbPowerToggle.IsChecked = UsbPower.Enabled;
	}

	private void OnPopupChanged(object sender, RoutedEventArgs e) => Save(() => Store.Current.Popup = PopupToggle.IsChecked == true);

	private void OnNotifyChanged(object sender, RoutedEventArgs e) => Save(() => Store.Current.Notify = NotifyToggle.IsChecked == true);

	private void OnAutoUpdateChanged(object sender, RoutedEventArgs e) => Save(() => Store.Current.AutoUpdate = AutoUpdateToggle.IsChecked == true);

	private void OnHotkeysChanged(object sender, RoutedEventArgs e) => Save(() =>
	{
		Store.Current.Hotkeys = HotkeyToggle.IsChecked == true;
		_setHotkeys?.Invoke(Store.Current.Hotkeys);
	});

	private void Save(Action change)
	{
		if (_loading)
		{
			return;
		}

		change();
		Store.Save();
	}

	private async void OnCheckUpdates(object sender, RoutedEventArgs e)
	{
		UpdateButton.IsEnabled = false;
		_release = null;
		Render();
		UpdateResult.Text = Localization.Get("langUpdateChecking");

		_release = await Updates.Check();
		Render();
		UpdateButton.IsEnabled = true;

		_switcher?.Log(_release.State switch
		{
			UpdateState.Newer => "langLogUpdateFound",
			UpdateState.Latest => "langLogUpdateNone",
			_ => "langLogUpdateFailed",
		}, _release.Version ?? Build.Version);
	}

	// Замена собственного файла: программа скачивает новую версию и уходит, а подменяет её
	// уже сценарий, который дожидается выхода. Если всё удалось, сюда управление не вернётся.
	private async void OnInstall(object sender, RoutedEventArgs e)
	{
		if (_release is not { State: UpdateState.Newer } release)
		{
			return;
		}

		InstallButton.IsEnabled = false;
		UpdateResult.Text = Localization.Get("langUpdateDownloading");

		var error = await Updates.Install(release, () => Application.Current.Shutdown());

		InstallButton.IsEnabled = true;
		if (error is not null)
		{
			UpdateResult.Text = error;
			_switcher?.Log("langLogUpdateFailed", error);
		}
	}

	private void Render()
	{
		UpdateChecked?.Invoke(_release);

		if (_release is not { } release)
		{
			UpdateResult.Text = "";
			InstallButton.Visibility = Visibility.Collapsed;

			return;
		}

		var newer = release.State == UpdateState.Newer;
		UpdateResult.Text = release.State switch
		{
			UpdateState.Newer => Localization.Format("langUpdateAvailable", release.Version!),
			UpdateState.Latest => Localization.Format("langUpdateLatest", Build.Version),
			_ => Localization.Get("langUpdateFailed"),
		};

		// Ставить нечего, если в релизе нет готового exe: тогда остаётся только ссылка.
		InstallButton.Visibility = newer && release.Download is not null ? Visibility.Visible : Visibility.Collapsed;
	}

	private void Warn(string message, string titleKey) =>
		Dialog.Say(Window.GetWindow(this)!, Localization.Get(titleKey), message);
}
