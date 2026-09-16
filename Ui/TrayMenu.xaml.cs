using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;
using WinFormsCursor = System.Windows.Forms.Cursor;

namespace AudioDirigent;

/// <summary>
/// Меню трея. Его рисует программа, а не WinForms: тот подсвечивает выбранное системным
/// синим поверх тёмной подложки, и по такой строке не понять, что именно выбрано. Здесь
/// та же поверхность, та же подсветка и те же скругления, что у всего остального.
/// </summary>
public partial class TrayMenu : Window
{
	// Поле под тень вокруг карточки: в расчёте места оно не участвует.
	private const double _gutter = 12;

	private readonly Action _open;
	private readonly Action _pause;
	private readonly Action _recover;
	private readonly Action _exit;

	internal TrayMenu(Action open, Action pause, Action recover, Action exit)
	{
		_open = open;
		_pause = pause;
		_recover = recover;
		_exit = exit;

		InitializeComponent();

		// Щелчок мимо меню закрывает его — как и у всякого меню.
		Deactivated += (_, _) => Hide();
	}

	/// <summary>Показать меню у курсора. Подписи собираются заново: и язык, и пауза меняются.</summary>
	internal void Popup(bool paused, bool recovering)
	{
		OpenItem.Content = Localization.Get("langTrayOpen");
		PauseItem.Content = Localization.Get(paused ? "langTrayResume" : "langTrayPause");
		RecoverItem.Content = Localization.Get("langTrayRecover");
		RecoverItem.IsEnabled = !recovering;
		ExitItem.Content = Localization.Get("langTrayExit");

		Show();
		Place();
		Activate();
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Hide();
		}

		base.OnPreviewKeyDown(e);
	}

	/// <summary>
	/// У курсора, не вылезая за рабочую область. Вниз меню разворачивается, если там есть
	/// место; у трея его обычно нет, и меню встаёт над курсором — как и системное.
	/// </summary>
	private void Place()
	{
		// Размер известен только после разметки: подписи у разных языков разной длины.
		UpdateLayout();

		var area = Screen.FromPoint(WinFormsCursor.Position).WorkingArea;
		var cursor = WinFormsCursor.Position;
		var dpi = VisualTreeHelper.GetDpi(this);

		var width = ActualWidth - (2 * _gutter);
		var height = ActualHeight - (2 * _gutter);

		var x = cursor.X / dpi.DpiScaleX;
		var y = cursor.Y / dpi.DpiScaleY;
		var left = area.Left / dpi.DpiScaleX;
		var top = area.Top / dpi.DpiScaleY;
		var right = area.Right / dpi.DpiScaleX;
		var bottom = area.Bottom / dpi.DpiScaleY;

		Left = Math.Clamp(x - (width / 2), left, Math.Max(left, right - width)) - _gutter;
		Top = Math.Clamp(y + height <= bottom ? y : y - height, top, Math.Max(top, bottom - height)) - _gutter;
	}

	// Пункт закрывает меню и только потом делает своё дело: иначе окно программы вылезало
	// бы из-под ещё живого меню, а само меню оставалось поверх него.
	private void Run(Action action)
	{
		Hide();
		action();
	}

	private void OnOpen(object sender, RoutedEventArgs e) => Run(_open);

	private void OnPause(object sender, RoutedEventArgs e) => Run(_pause);

	private void OnRecover(object sender, RoutedEventArgs e) => Run(_recover);

	private void OnExit(object sender, RoutedEventArgs e) => Run(_exit);
}
