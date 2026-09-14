using System.Windows;
using System.Windows.Input;

namespace AudioDirigent;

/// <summary>
/// Вопрос или предупреждение в виде самой программы. Системный MessageBox рисует Windows,
/// и посреди окна без рамок он выглядит гостем из другой программы — а спрашивают им как
/// раз о том, что эта программа собирается сделать.
/// </summary>
public partial class Dialog : Window
{
	private Dialog() => InitializeComponent();

	/// <summary>Спросить с возможностью отказаться; false — отказались.</summary>
	internal static bool Ask(Window owner, string title, string text) => Open(owner, title, text, ask: true);

	/// <summary>Сообщить: отвечать нечего, остаётся прочесть.</summary>
	internal static void Say(Window owner, string title, string text) => Open(owner, title, text, ask: false);

	private static bool Open(Window owner, string title, string text, bool ask)
	{
		var dialog = new Dialog
		{
			Owner = owner,
			// Заголовок окна не виден — рамки нет, — но по нему программу находит Alt+Tab.
			Title = title,
		};

		dialog.Heading.Text = title;
		dialog.Message.Text = text;
		dialog.CancelButton.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;

		return dialog.ShowDialog() == true;
	}

	// Esc отвечает «нет» и там, где кнопки отказа нет: закрыть прочитанное им привычнее всего.
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			DialogResult = false;
		}

		base.OnPreviewKeyDown(e);
	}

	protected override void OnSourceInitialized(System.EventArgs e)
	{
		base.OnSourceInitialized(e);
		OkButton.Focus();
	}

	// Рамки у окна нет — тащат его за саму карточку.
	private void OnDrag(object sender, MouseButtonEventArgs e) => DragMove();

	private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

	private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
