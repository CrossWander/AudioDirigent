using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace AudioDirigent;

/// <summary>Заголовок окна: имя программы и кнопки. Рамки у окна нет, эта полоса её заменяет.</summary>
public partial class TitleBar : UserControl
{
	public TitleBar() => InitializeComponent();

	/// <summary>Нажата кнопка настроек — окно решает, где раскрыть их список.</summary>
	public event Action<FrameworkElement>? Settings;

	/// <summary>Нажат знак Bluetooth.</summary>
	public event Action<FrameworkElement>? Nearby;

	/// <summary>Щёлкнули по значку состояния — окно ставит и снимает паузу.</summary>
	public event Action? PauseToggled;

	public event Action? Minimise;

	public event Action? Maximise;

	public event Action? Close;

	private void OnSettings(object sender, RoutedEventArgs e) => Settings?.Invoke((FrameworkElement)sender);

	private void OnNearby(object sender, RoutedEventArgs e) => Nearby?.Invoke((FrameworkElement)sender);

	/// <summary>Чем программа занята: следит или стоит на паузе.</summary>
	internal void ShowState(bool paused)
	{
		StateText.Text = Localization.Get(paused ? "langPausedBadge" : "langWatching");
		StateDot.Fill = (Brush)FindResource(paused ? "Muted" : "Good");
		StateBadge.Background = (Brush)FindResource(paused ? "BadgeFill" : "AccentFaint");
	}

	private void OnBadgeClick(object sender, MouseButtonEventArgs e) => PauseToggled?.Invoke();

	private void OnMinimise(object sender, RoutedEventArgs e) => Minimise?.Invoke();

	private void OnMaximise(object sender, RoutedEventArgs e) => Maximise?.Invoke();

	private void OnClose(object sender, RoutedEventArgs e) => Close?.Invoke();
}
