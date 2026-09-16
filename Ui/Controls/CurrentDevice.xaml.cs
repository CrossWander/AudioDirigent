using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioDirigent;

/// <summary>Шапка окна: что играет сейчас, что говорит приёмник и следит ли программа за звуком.</summary>
public partial class CurrentDevice : UserControl
{
	private Switcher? _switcher;

	public CurrentDevice() => InitializeComponent();

	/// <summary>Щёлкнули по значку состояния — окно ставит и снимает паузу.</summary>
	public event Action? PauseToggled;

	internal void Attach(Switcher switcher) => _switcher = switcher;

	internal void Refill(EDataFlow flow)
	{
		if (_switcher is not { } switcher)
		{
			return;
		}

		var devices = Endpoints.All(flow);
		DeviceName.Text = Endpoints.Current(flow)?.Name ?? Localization.Get("langNone");
		DeviceCount.Text = Localization.Format("langActiveOf",
			devices.Count(device => device.State == DeviceState.Active), devices.Count);

		(ProbeStatus.Text, var colour) = (switcher.HasProbe, switcher.DeviceOn) switch
		{
			(false, _) => (Localization.Get("langProbeNotFound"), "Muted"),
			(true, true) => (Localization.Get("langProbeOn"), "Good"),
			(true, false) => (Localization.Get("langProbeOff"), "Warn"),
			(true, null) => (Localization.Get("langProbeSilent"), "Muted"),
		};

		ProbeDot.Fill = Paint(colour);
		RefreshBadge();
	}

	internal void RefreshBadge()
	{
		var paused = _switcher?.Paused == true;

		StateText.Text = Localization.Get(paused ? "langPausedBadge" : "langWatching");
		StateDot.Fill = Paint(paused ? "Muted" : "Good");
		StateBadge.Background = paused ? Paint("BadgeFill") : Paint("AccentFaint");
	}

	private Brush Paint(string key) => (Brush)FindResource(key);

	private void OnBadgeClick(object sender, MouseButtonEventArgs e) => PauseToggled?.Invoke();
}
