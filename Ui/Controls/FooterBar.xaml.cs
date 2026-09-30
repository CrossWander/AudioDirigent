using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioDirigent;

/// <summary>Подвал окна: кто написал, на каком языке говорить и есть ли новая версия.</summary>
public partial class FooterBar : UserControl
{
	private bool _switching;
	private bool _newer;

	public FooterBar()
	{
		InitializeComponent();
		Credits.Text = $"Developed by Dykalo Pavlo, 2026   ·   {Build.Version}";
		SelectLanguage();
	}

	/// <summary>
	/// Сколько устройств живо и что говорит приёмник. Это стояло в шапке рядом с именем
	/// устройства, к которому не относится: счёт — про список, а не про то, что звучит.
	/// </summary>
	internal void Status(int active, int total, bool hasProbe, bool? deviceOn)
	{
		Counts.Text = Localization.Format("langActiveOf", active, total);

		(ProbeStatus.Text, var colour) = deviceOn switch
		{
			true => (Localization.Get("langProbeOn"), "Good"),
			false => (Localization.Get("langProbeOff"), "Warn"),
			_ => (Localization.Get("langProbeSilent"), "Muted"),
		};

		ProbeDot.Fill = (System.Windows.Media.Brush)FindResource(colour);
		ProbeDot.Visibility = hasProbe ? Visibility.Visible : Visibility.Collapsed;
		ProbeStatus.Visibility = hasProbe ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>Нажали «Журнал»: открыть его должно окно — панель живёт не здесь.</summary>
	internal event System.Action? LogToggled;

	/// <summary>Стрелка смотрит вниз, пока журнал открыт.</summary>
	internal void ShowLog(bool open) =>
		LogArrow.RenderTransform = new System.Windows.Media.RotateTransform(open ? 90 : 0, 4.5, 4.5);

	private void OnLog(object sender, RoutedEventArgs e) => LogToggled?.Invoke();

	/// <summary>Отметить язык, выбранный не отсюда.</summary>
	internal void SelectLanguage()
	{
		_switching = true;
		LangRu.IsChecked = Localization.Current == "ru";
		LangUk.IsChecked = Localization.Current == "uk";
		LangEn.IsChecked = Localization.Current == "en";
		_switching = false;
	}

	internal void Show(Release? release)
	{
		_newer = release is { State: UpdateState.Newer };
		UpdateLink.Text = _newer ? Localization.Format("langUpdateAvailable", release!.Version!) : "";
		UpdateLink.Visibility = _newer ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OnLanguage(object sender, RoutedEventArgs e)
	{
		if (!_switching && sender is RadioButton { Tag: string code } && code != Localization.Current)
		{
			Localization.Apply(code);
		}
	}

	private void OnOpenReleases(object sender, MouseButtonEventArgs e)
	{
		if (_newer)
		{
			Process.Start(new ProcessStartInfo(Updates.Releases) { UseShellExecute = true });
		}
	}
}
