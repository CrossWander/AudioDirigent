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
