using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using WinFormsCursor = System.Windows.Forms.Cursor;

namespace AudioDirigent;

/// <summary>
/// Карточка устройства над треем: что подключилось, играет ли оно теперь и сколько в нём
/// заряда, если Windows его знает. Появляется на подключение и на пропажу — на то самое
/// событие, ради которого человек и смотрит на экран.
/// </summary>
public partial class DevicePopup : Window
{
	// Отступ от угла экрана. Восемнадцать точек снаружи карточки занимает поле под тень,
	// так что от края её отделяет привычная для уведомлений четверть сотни.
	private const double _margin = 6;

	private static readonly TimeSpan _life = TimeSpan.FromSeconds(4);

	// Наведение — не событие: карточка висит, пока курсор на значке, и уходит, когда он
	// ушёл. Срока жизни у неё нет: значок не шлёт события «мышь ушла» вовсе, а события
	// движения перестают приходить, стоит руке замереть, — поэтому следим за курсором сами.
	private static readonly TimeSpan _watch = TimeSpan.FromMilliseconds(150);

	/// <summary>
	/// Насколько далеко курсор ещё считается стоящим на том же значке. Значок в трее —
	/// это два десятка точек в поперечнике, соседний стоит сразу за ним.
	/// </summary>
	internal const int Reach = 26;
	private static readonly Duration _rise = new(TimeSpan.FromMilliseconds(380));
	private static readonly Duration _fall = new(TimeSpan.FromMilliseconds(220));

	private readonly Switcher _switcher;
	private readonly DispatcherTimer _timer;
	private AudioEndpoint? _device;
	private TimeSpan _span = _life;

	// Карточку позвали наведением: она живёт по курсору, а не по часам.
	private bool _glancing;
	private System.Drawing.Point _anchor;

	internal DevicePopup(Switcher switcher)
	{
		_switcher = switcher;
		InitializeComponent();

		_timer = new DispatcherTimer { Interval = _life };
		_timer.Tick += (_, _) => Tick();

		// Пока мышь на карточке, она не исчезает: иначе кнопку было бы не нажать —
		// человек ведёт к ней курсор ровно те секунды, что карточка живёт.
		MouseEnter += (_, _) => _timer.Stop();
		MouseLeave += (_, _) => Restart();
	}

	/// <summary>Показать карточку устройства. Повторный вызов заменяет содержимое и продлевает показ.</summary>
	internal void Announce(AudioEndpoint device, bool arrived, bool becameDefault)
	{
		_span = _life;
		_glancing = false;

		Fill(device, arrived, becameDefault);

		Show();
		Place();
		Rise();
		Restart();
	}

	/// <summary>
	/// Показать то, что звучит сейчас: карточку спросили наведением на значок, а не
	/// событием. Устройство уже главное, решать нечего — кнопка не появится, и срок
	/// показа короче.
	/// </summary>
	internal void Glance(AudioEndpoint device)
	{
		_span = _watch;
		_glancing = true;
		_anchor = WinFormsCursor.Position;

		// Наведение приходит на каждое движение мыши. Поднимать уже поднятую карточку
		// заново значило бы дёргать её всё время, что курсор стоит на значке.
		if (IsVisible && _device?.Id == device.Id)
		{
			Restart();

			return;
		}

		Fill(device, arrived: true, becameDefault: true);

		Show();
		Place();
		Rise();
		Restart();
	}

	/// <summary>
	/// Собрать карточку, не показывая её. Отдельно от показа ради самопроверки: имя рисунка,
	/// которого нет в словаре, иначе всплыло бы только при первом подключении устройства —
	/// у пользователя, а не при сборке.
	/// </summary>
	internal void Fill(AudioEndpoint device, bool arrived, bool becameDefault)
	{
		_device = device;

		DeviceIcon.Data = (Geometry)FindResource(device.Form switch
		{
			FormFactor.Headphones or FormFactor.Headset => "IconHeadphones",
			FormFactor.Microphone or FormFactor.Handset => "IconMicrophone",
			FormFactor.Speakers or FormFactor.LineLevel => "IconSpeakers",
			FormFactor.Hdmi or FormFactor.Spdif or FormFactor.DigitalPassthrough => "IconScreen",
			_ => "IconSound",
		});

		DeviceName.Text = device.Name;
		Status.Text = Localization.Get((arrived, becameDefault) switch
		{
			(false, _) => "langPopupGone",
			(true, true) => "langPopupDefault",
			(true, false) => "langPopupConnected",
		});

		Charge.Show(arrived ? Battery.Of(device.Node) : null);
		ShowAction(device, arrived, becameDefault);
	}

	/// <summary>
	/// Кнопка появляется ровно тогда, когда есть что решать: устройство подключилось само,
	/// правила о нём ничего не знают, и звук остался там, где был. Воткнутое в разъём звучит
	/// и без вопросов, а устройство из списка приоритетов разберёт правило.
	/// </summary>
	private void ShowAction(AudioEndpoint device, bool arrived, bool becameDefault)
	{
		var config = _switcher.Rules.For(device.Flow);
		var offer = arrived
			&& !becameDefault
			&& config.Rank(device) < 0
			&& !config.Blocks(device)
			&& !device.HandsFree;

		Action.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
		Action.Content = Localization.Get("langPopupMakeMain");
	}

	private void OnAction(object sender, RoutedEventArgs e)
	{
		if (_device is not { } device)
		{
			return;
		}

		// Сначала правило, потом переключение: иначе пересчёт правил, который идёт следом
		// за сменой устройства, увёл бы звук обратно — устройства-то в приоритетах ещё нет.
		var rules = _switcher.Rules;
		_switcher.Save(rules.With(device.Flow, rules.For(device.Flow).Promote(device)));
		Endpoints.MakeCurrent(device);

		Dismiss();
	}

	/// <summary>Нижний правый угол рабочей области того экрана, где сейчас курсор.</summary>
	private void Place()
	{
		var area = Screen.FromPoint(WinFormsCursor.Position).WorkingArea;
		var dpi = VisualTreeHelper.GetDpi(this);

		// Высота карточки зависит от того, влезли ли в неё заряд и кнопка: меряем по факту.
		UpdateLayout();

		Left = (area.Right / dpi.DpiScaleX) - Width - _margin;
		Top = (area.Bottom / dpi.DpiScaleY) - ActualHeight - _margin;
	}

	private void Rise()
	{
		BeginAnimation(OpacityProperty, new DoubleAnimation(1, _rise));
		Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ActualHeight, 0, _rise)
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
		});
	}

	// Час карточки пробил. Позванная событием — уходит; позванная наведением — только
	// если курсор ушёл со значка и не перебрался на неё саму.
	private void Tick()
	{
		if (!_glancing)
		{
			Dismiss();

			return;
		}

		var now = WinFormsCursor.Position;

		if (IsMouseOver || Near(now, _anchor))
		{
			return;
		}

		Dismiss();
	}

	/// <summary>Курсор не сходил со значка: отойти дальше его ширины — это уже уйти.</summary>
	internal static bool Near(System.Drawing.Point now, System.Drawing.Point then) =>
		Math.Abs(now.X - then.X) <= Reach && Math.Abs(now.Y - then.Y) <= Reach;

	private void Restart()
	{
		_timer.Interval = _span;
		_timer.Stop();
		_timer.Start();
	}

	private void Dismiss()
	{
		_timer.Stop();
		_glancing = false;

		var slide = new DoubleAnimation(ActualHeight, _fall) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
		var fade = new DoubleAnimation(0, _fall);

		// Прячем окно, а не закрываем: следующее устройство подключится к тому же окну,
		// и создавать его заново — это ещё и мигание на экране.
		fade.Completed += (_, _) => Hide();

		Slide.BeginAnimation(TranslateTransform.YProperty, slide);
		BeginAnimation(OpacityProperty, fade);
	}
}
