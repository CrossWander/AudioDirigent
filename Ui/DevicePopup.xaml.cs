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
	private static readonly Duration _rise = new(TimeSpan.FromMilliseconds(380));
	private static readonly Duration _fall = new(TimeSpan.FromMilliseconds(220));

	private readonly Switcher _switcher;
	private readonly DispatcherTimer _timer;
	private AudioEndpoint? _device;

	internal DevicePopup(Switcher switcher)
	{
		_switcher = switcher;
		InitializeComponent();

		_timer = new DispatcherTimer { Interval = _life };
		_timer.Tick += (_, _) => Dismiss();

		// Пока мышь на карточке, она не исчезает: иначе кнопку было бы не нажать —
		// человек ведёт к ней курсор ровно те секунды, что карточка живёт.
		MouseEnter += (_, _) => _timer.Stop();
		MouseLeave += (_, _) => Restart();
	}

	/// <summary>Показать карточку устройства. Повторный вызов заменяет содержимое и продлевает показ.</summary>
	internal void Announce(AudioEndpoint device, bool arrived, bool becameDefault)
	{
		Fill(device, arrived, becameDefault);

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

		ShowBattery(arrived ? Battery.Percent(device.Node) : null);
		ShowAction(device, arrived, becameDefault);
	}

	private void ShowBattery(int? percent)
	{
		if (percent is not { } level)
		{
			BatteryRow.Visibility = Visibility.Collapsed;
			return;
		}

		BatteryRow.Visibility = Visibility.Visible;
		BatteryText.Text = $"{level}%";

		// Полоса рисуется долей от ширины корпуса за вычетом его же полей.
		BatteryFill.Width = Math.Max(1, (22 - 5) * Math.Clamp(level, 0, 100) / 100.0);
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

	private void Restart()
	{
		_timer.Stop();
		_timer.Start();
	}

	private void Dismiss()
	{
		_timer.Stop();

		var slide = new DoubleAnimation(ActualHeight, _fall) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
		var fade = new DoubleAnimation(0, _fall);

		// Прячем окно, а не закрываем: следующее устройство подключится к тому же окну,
		// и создавать его заново — это ещё и мигание на экране.
		fade.Completed += (_, _) => Hide();

		Slide.BeginAnimation(TranslateTransform.YProperty, slide);
		BeginAnimation(OpacityProperty, fade);
	}
}
