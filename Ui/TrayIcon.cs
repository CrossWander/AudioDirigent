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
	// Меню рисует WinForms, до кистей окна ему не достать — цвета тёмной темы задаются здесь.
	private static readonly Color _surface = Color.FromArgb(0x17, 0x1A, 0x21);
	private static readonly Color _text = Color.FromArgb(0xE8, 0xEA, 0xF0);
	private static readonly Color _hover = Color.FromArgb(0x23, 0x29, 0x36);
	private static readonly Color _line = Color.FromArgb(0x26, 0x2B, 0x36);

	// Смена устройства — единственное событие, ради которого стоит показываться поверх всего.
	private static readonly string[] _switchKeys =
		["langLogSwitched", "langLogSwitchedFromNone", "langLogSwitchedIn", "langLogSwitchedInFromNone"];

	private readonly Switcher _switcher = new();
	private readonly NotifyIcon _icon;
	private readonly Icon _iconActive = LoadIcon("app.ico");
	private readonly Icon _iconPaused = LoadIcon("app-paused.ico");
	private readonly ToolStripMenuItem _openItem = new();
	private readonly ToolStripMenuItem _pauseItem = new();
	private readonly ToolStripMenuItem _recoverItem = new();
	private readonly ToolStripMenuItem _exitItem = new();
	private MainWindow? _window;
	private Hotkeys? _hotkeys;
	private DevicePopup? _popup;

	public TrayIcon(bool showWindow)
	{
		var menu = new ContextMenuStrip
		{
			Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
			BackColor = _surface,
			ForeColor = _text,
			ShowImageMargin = false,
		};

		_openItem.Font = new Font(menu.Font, FontStyle.Bold);
		_openItem.Click += (_, _) => ShowWindow();
		_pauseItem.Click += (_, _) => TogglePause();
		_recoverItem.Click += (_, _) => Recover();
		_exitItem.Click += (_, _) => Application.Current.Shutdown();

		menu.Items.Add(_openItem);
		menu.Items.Add(_pauseItem);
		menu.Items.Add(_recoverItem);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_exitItem);

		Localization.Changed += UpdateMenuText;
		UpdateMenuText();

		_icon = new NotifyIcon
		{
			Icon = _iconActive,
			Text = "AudioDirigent",
			Visible = true,
			ContextMenuStrip = menu,
		};
		_icon.DoubleClick += (_, _) => ShowWindow();

		_switcher.Logged += ShowInTooltip;
		_switcher.Arrived += (device, isDefault) => Announce(device, arrived: true, isDefault);
		_switcher.Left += device => Announce(device, arrived: false, becameDefault: false);
		_switcher.Start();
		_switcher.Log("langLogBuild", Build.Version);
		SetHotkeys(Store.Current.Hotkeys);

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
		var text = $"AudioDirigent — {entry.Render()}";
		_icon.Text = text.Length <= 63 ? text : text[..63];

		if (Store.Current.Notify && _switchKeys.Contains(entry.Key)
			&& !Balloon.Show(_icon, "AudioDirigent", entry.Text))
		{
			_icon.ShowBalloonTip(4000, "AudioDirigent", entry.Text, ToolTipIcon.None);
		}
	});

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

	private void UpdateMenuText()
	{
		_openItem.Text = Localization.Get("langTrayOpen");
		_pauseItem.Text = Localization.Get(_switcher.Paused ? "langTrayResume" : "langTrayPause");
		_recoverItem.Text = Localization.Get("langTrayRecover");
		_exitItem.Text = Localization.Get("langTrayExit");
	}

	// Восстановление переустанавливает устройства и может перезапустить службу звука —
	// на потоке интерфейса это заморозило бы и меню, и трей на несколько секунд.
	private void Recover()
	{
		_recoverItem.Enabled = false;

		// Пункт меню возвращаем через диспетчер, а не через await: контекст синхронизации
		// у обработчика WinForms не гарантирован, а зависшее меню трея не починить ничем.
		Task.Run(() =>
		{
			_switcher.Recover();
			Application.Current.Dispatcher.BeginInvoke(() => _recoverItem.Enabled = true);
		});
	}

	private void TogglePause()
	{
		_switcher.Paused = !_switcher.Paused;
		_icon.Icon = _switcher.Paused ? _iconPaused : _iconActive;
		UpdateMenuText();
		_switcher.Log(_switcher.Paused ? "langLogPaused" : "langLogResumed");

		if (!_switcher.Paused)
		{
			_switcher.Apply();
		}

		_window?.SyncPauseState();
	}

	private void ShowWindow()
	{
		_window ??= new MainWindow(_switcher, TogglePause, SetHotkeys);
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
		Localization.Changed -= UpdateMenuText;
		_hotkeys?.Dispose();
		_popup?.Close();
		_window?.Detach();
		_icon.Visible = false;
		_icon.Dispose();
		_switcher.Dispose();
		_iconActive.Dispose();
		_iconPaused.Dispose();
	}

	/// <summary>Меню трея рисуется WinForms — красим под тёмное окно.</summary>
	private sealed class DarkMenuColors : ProfessionalColorTable
	{
		public override Color ToolStripDropDownBackground => _surface;
		public override Color ImageMarginGradientBegin => _surface;
		public override Color ImageMarginGradientMiddle => _surface;
		public override Color ImageMarginGradientEnd => _surface;
		public override Color MenuItemSelected => _hover;
		public override Color MenuItemSelectedGradientBegin => _hover;
		public override Color MenuItemSelectedGradientEnd => _hover;
		public override Color MenuItemBorder => _line;
		public override Color MenuBorder => _line;
		public override Color SeparatorDark => _line;
		public override Color SeparatorLight => _line;
	}
}
