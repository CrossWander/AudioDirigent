using System.Windows.Controls;
using System.Windows.Media;

namespace AudioDirigent;

/// <summary>Шапка окна: что играет сейчас и следит ли программа за звуком.</summary>
public partial class CurrentDevice : UserControl
{
	private Switcher? _switcher;

	public CurrentDevice() => InitializeComponent();

	internal void Attach(Switcher switcher) => _switcher = switcher;

	internal void Refill(EDataFlow flow)
	{
		if (_switcher is null)
		{
			return;
		}

		var current = Endpoints.Current(flow);

		DeviceName.Text = current?.Name ?? Localization.Get("langNone");
		StateDot.Fill = Paint(current is null ? "Muted" : "Good");
		Charge.Show(Battery.Of(current?.Node));
	}

	private Brush Paint(string key) => (Brush)FindResource(key);
}
