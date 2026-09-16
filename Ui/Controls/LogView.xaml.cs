using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AudioDirigent;

/// <summary>
/// Журнал переключений. Записи держим записями, а не готовым текстом: иначе при смене языка
/// строки остались бы на том, при котором произошли.
/// </summary>
public partial class LogView : UserControl
{
	private const int _keep = 200;

	private readonly List<LogEntry> _entries = [];

	public LogView()
	{
		InitializeComponent();
		_entries.AddRange(Journal.Recent(40));
		Render();
	}

	/// <summary>Панель раскрылась или свернулась: окно подрастает ровно на её высоту.</summary>
	public event Action<double>? HeightWanted;

	internal void Add(LogEntry entry)
	{
		_entries.Add(entry);
		if (_entries.Count > _keep)
		{
			_entries.RemoveRange(0, _entries.Count - _keep);
		}

		Render();
	}

	internal void Render()
	{
		Text.Text = string.Join(Environment.NewLine, _entries.Select(Localization.Of));
		Scroll.ScrollToEnd();
	}

	private void OnExpanded(object sender, RoutedEventArgs e) => HeightWanted?.Invoke(+Extra());

	private void OnCollapsed(object sender, RoutedEventArgs e) => HeightWanted?.Invoke(-Extra());

	// Высота панели вместе с отступом: раскрытый журнал не должен съедать список устройств.
	private static double Extra() => 150 + 8;
}
