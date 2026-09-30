using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioDirigent;

/// <summary>Шапка окна: что играет сейчас и следит ли программа за звуком.</summary>
public partial class CurrentDevice : UserControl
{
	private Switcher? _switcher;

	public CurrentDevice() => InitializeComponent();

	/// <summary>Щёлкнули по значку состояния — окно ставит и снимает паузу.</summary>
	public event Action? PauseToggled;

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

		RefreshBadge();
	}

	internal void RefreshBadge()
	{
		var paused = _switcher?.Paused == true;

		StateText.Text = Localization.Get(paused ? "langPausedBadge" : "langWatching");
		StateBadgeDot.Fill = Paint(paused ? "Muted" : "Good");
		StateBadge.Background = paused ? Paint("BadgeFill") : Paint("AccentFaint");
	}

	private Brush Paint(string key) => (Brush)FindResource(key);

	private void OnBadgeClick(object sender, MouseButtonEventArgs e) => PauseToggled?.Invoke();
}
