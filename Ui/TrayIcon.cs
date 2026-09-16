using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace AudioDirigent;

/// <summary>Иконка в трее: владеет переключателем и окном, живёт всё время работы программы.</summary>
internal sealed class TrayIcon : IDisposable
{
	// Смена устройства — единственное событие, ради которого стоит показываться поверх всего.
	private static readonly string[] _switchKeys =
		["langLogSwitched", "langLogSwitchedFromNone", "langLogSwitchedIn", "langLogSwitchedInFromNone"];

	private readonly Switcher _switcher = new();
	private readonly NotifyIcon _icon;
	private readonly Icon _iconActive = LoadIcon("app.ico");
	private readonly Icon _iconPaused = LoadIcon("app-paused.ico");
	private TrayMenu? _menu;
	private bool _recovering;
	private MainWindow? _window;
	private Hotkeys? _hotkeys;
	private DevicePopup? _popup;
	private Release? _update;

	public TrayIcon(bool showWindow)
	{
		_icon = new NotifyIcon
		{
			Icon = _iconActive,
			Text = "AudioDirigent",
			Visible = true,
		};

		_icon.DoubleClick += (_, _) => ShowWindow();

		// Меню строит и показывает программа: своё окно вместо чужого по правой кнопке.
		_icon.MouseUp += (_, e) =>
		{
			if (e.Button == MouseButtons.Right)
			{
				_menu ??= new TrayMenu(ShowWindow, TogglePause, Recover, Application.Current.Shutdown);
				_menu.Popup(_switcher.Paused, _recovering);
			}
		};

		_switcher.Logged += ShowInTooltip;
		_switcher.Arrived += (device, isDefault) => Announce(device, arrived: true, isDefault);
		_switcher.Left += device => Announce(device, arrived: false, becameDefault: false);
		_switcher.Start();
		_switcher.Log("langLogBuild", Build.Version);
		SetHotkeys(Store.Current.Hotkeys);
		CheckUpdates();

		if (showWindow)
		{
			ShowWindow();
		}
	}

	// Карточка устройства создаётся при первом подключении и дальше живёт: показать её
	// снова дешевле, чем строить окно заново, да и мигания при этом нет.
	private void Announce(AudioEndpoint device, bool arrived, bool becameDefault) =>
		Application.Current.Dispatcher.BeginInvoke(() =>
		{
			if (!Store.Current.Popup)
			{
				return;
			}

			_popup ??= new DevicePopup(_switcher);
			_popup.Announce(device, arrived, becameDefault);
		});

	// Журнал приходит из потока таймера и из COM-колбэка, а NotifyIcon — контрол WinForms:
	// его свойства можно трогать только с того потока, где он создан.
	private void ShowInTooltip(LogEntry entry) => Application.Current.Dispatcher.BeginInvoke(() =>
	{
		var text = $"AudioDirigent — {Localization.Of(entry)}";
		_icon.Text = text.Length <= 63 ? text : text[..63];

		var said = Localization.Of(entry.Message);
		if (Store.Current.Notify && _switchKeys.Contains(entry.Message.Key)
			&& !Balloon.Show(_icon, "AudioDirigent", said))
		{
			_icon.ShowBalloonTip(4000, "AudioDirigent", said, ToolTipIcon.None);
		}
	});

	// Ежедневная проверка, если её включили: единственный раз, когда программа идёт в сеть
	// без просьбы. Молча — новость о новой версии ждёт в подвале открытого окна.
	private void CheckUpdates()
	{
		if (!Updates.Due)
		{
			return;
		}

		Task.Run(async () =>
		{
			_update = await Updates.Check();

			if (_update.State == UpdateState.Newer)
			{
				_switcher.Log("langLogUpdateFound", _update.Version!);
				_window?.ShowUpdate(_update);
			}
		});
	}

	// Сочетания видит вся система, поэтому они включаются по просьбе, а не сами собой.
	private void SetHotkeys(bool enabled)
	{
		_hotkeys?.Dispose();
		_hotkeys = enabled
			? new Hotkeys(_switcher.Log,
				(Store.Current.PauseHotkey, TogglePause),
				(Store.Current.RecoverHotkey, Recover))
			: null;
	}

	// Восстановление переустанавливает устройства и может перезапустить службу звука —
	// на потоке интерфейса это заморозило бы и меню, и трей на несколько секунд.
	private void Recover()
	{
		_recovering = true;

		// Отметку снимаем через диспетчер, а не через await: контекст синхронизации у
		// обработчика WinForms не гарантирован, а меню читает её с потока интерфейса.
		Task.Run(() =>
		{
			_switcher.Recover();
			Application.Current.Dispatcher.BeginInvoke(() => _recovering = false);
		});
	}

	private void TogglePause()
	{
		_switcher.Paused = !_switcher.Paused;
		_icon.Icon = _switcher.Paused ? _iconPaused : _iconActive;
		_switcher.Log(_switcher.Paused ? "langLogPaused" : "langLogResumed");

		if (!_switcher.Paused)
		{
			_switcher.Apply();
		}

		_window?.SyncPauseState();
	}

	private void ShowWindow()
	{
		if (_window is null)
		{
			_window = new MainWindow(_switcher, TogglePause, SetHotkeys);
			_window.ShowUpdate(_update);
		}

		_window.Show();
		_window.WindowState = System.Windows.WindowState.Normal;
		_window.Activate();
		_window.AnimateIn();
	}

	private static Icon LoadIcon(string name)
	{
		using var stream = Application.GetResourceStream(new Uri(name, UriKind.Relative))!.Stream;
		return new Icon(stream);
	}

	public void Dispose()
	{
		_hotkeys?.Dispose();
		_menu?.Close();
		_popup?.Close();
		_window?.Detach();
		_icon.Visible = false;
		_icon.Dispose();
		_switcher.Dispose();
		_iconActive.Dispose();
		_iconPaused.Dispose();
	}
}
