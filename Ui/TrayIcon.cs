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
	// Меню рисует WinForms, до кистей окна ему не достать — цвета берём из той же палитры
	// руками. Обе темы описаны здесь, потому что выбирать между ними приходится на лету.
	private static Color Surface => Theme.Dark ? Color.FromArgb(0x1C, 0x1C, 0x1E) : Color.White;

	private static Color Text => Theme.Dark ? Color.FromArgb(0xEC, 0xEC, 0xF0) : Color.FromArgb(0x1C, 0x1C, 0x1E);

	private static Color Hover => Theme.Dark ? Color.FromArgb(0x2C, 0x2C, 0x30) : Color.FromArgb(0xED, 0xED, 0xF2);

	private static Color Line => Theme.Dark ? Color.FromArgb(0x33, 0x33, 0x38) : Color.FromArgb(0xE0, 0xE0, 0xE6);

	// Смена устройства — единственное событие, ради которого стоит показываться поверх всего.
	private static readonly string[] _switchKeys =
		["langLogSwitched", "langLogSwitchedFromNone", "langLogSwitchedIn", "langLogSwitchedInFromNone"];

	private readonly Switcher _switcher = new();
	private readonly ContextMenuStrip _menu;
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
	private Release? _update;

	public TrayIcon(bool showWindow)
	{
		_menu = new ContextMenuStrip
		{
			Renderer = new ToolStripProfessionalRenderer(new MenuColours()),
			ShowImageMargin = false,
		};

		Repaint();
		Theme.Changed += Repaint;

		_openItem.Font = new Font(_menu.Font, FontStyle.Bold);
		_openItem.Click += (_, _) => ShowWindow();
		_pauseItem.Click += (_, _) => TogglePause();
		_recoverItem.Click += (_, _) => Recover();
		_exitItem.Click += (_, _) => Application.Current.Shutdown();

		_menu.Items.Add(_openItem);
		_menu.Items.Add(_pauseItem);
		_menu.Items.Add(_recoverItem);
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add(_exitItem);

		Localization.Changed += UpdateMenuText;
		UpdateMenuText();

		_icon = new NotifyIcon
		{
			Icon = _iconActive,
			Text = "AudioDirigent",
			Visible = true,
			ContextMenuStrip = _menu,
		};
		_icon.DoubleClick += (_, _) => ShowWindow();

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
		var text = $"AudioDirigent — {entry.Render()}";
		_icon.Text = text.Length <= 63 ? text : text[..63];

		if (Store.Current.Notify && _switchKeys.Contains(entry.Key)
			&& !Balloon.Show(_icon, "AudioDirigent", entry.Text))
		{
			_icon.ShowBalloonTip(4000, "AudioDirigent", entry.Text, ToolTipIcon.None);
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

	// Цвета меню заданы руками: под чужой темой оно осталось бы тёмным на светлом окне.
	private void Repaint()
	{
		_menu.BackColor = Surface;
		_menu.ForeColor = Text;
		_menu.Invalidate();
	}

	public void Dispose()
	{
		Localization.Changed -= UpdateMenuText;
		Theme.Changed -= Repaint;
		_hotkeys?.Dispose();
		_popup?.Close();
		_window?.Detach();
		_icon.Visible = false;
		_icon.Dispose();
		_switcher.Dispose();
		_iconActive.Dispose();
		_iconPaused.Dispose();
	}

	/// <summary>Меню трея рисуется WinForms — красим его под окно, какой бы теме оно ни следовало.</summary>
	private sealed class MenuColours : ProfessionalColorTable
	{
		public override Color ToolStripDropDownBackground => Surface;
		public override Color ImageMarginGradientBegin => Surface;
		public override Color ImageMarginGradientMiddle => Surface;
		public override Color ImageMarginGradientEnd => Surface;
		public override Color MenuItemSelected => Hover;
		public override Color MenuItemSelectedGradientBegin => Hover;
		public override Color MenuItemSelectedGradientEnd => Hover;
		public override Color MenuItemBorder => Line;
		public override Color MenuBorder => Line;
		public override Color SeparatorDark => Line;
		public override Color SeparatorLight => Line;
	}
}
