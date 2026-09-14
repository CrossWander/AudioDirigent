using System;
using System.Windows;
using System.Windows.Controls;

namespace AudioDirigent;

/// <summary>Заголовок окна: имя программы и кнопки. Рамки у окна нет, эта полоса её заменяет.</summary>
public partial class TitleBar : UserControl
{
	public TitleBar() => InitializeComponent();

	/// <summary>Нажата кнопка настроек — окно решает, где раскрыть их список.</summary>
	public event Action<FrameworkElement>? Settings;

	public event Action? Minimise;

	public event Action? Maximise;

	public event Action? Close;

	private void OnSettings(object sender, RoutedEventArgs e) => Settings?.Invoke((FrameworkElement)sender);

	private void OnMinimise(object sender, RoutedEventArgs e) => Minimise?.Invoke();

	private void OnMaximise(object sender, RoutedEventArgs e) => Maximise?.Invoke();

	private void OnClose(object sender, RoutedEventArgs e) => Close?.Invoke();
}
