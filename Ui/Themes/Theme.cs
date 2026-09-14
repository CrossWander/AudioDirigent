using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AudioDirigent;

/// <summary>Что выбрано в настройках, а не что показано: «как в системе» — тоже выбор.</summary>
internal enum ThemeMode
{
	Auto,
	Light,
	Dark,
}

/// <summary>
/// Тема интерфейса. Устроена так же, как язык: словарь подменяется целиком, а разметка
/// обращается к ролям — «полотно», «поверхность», «приглушённый текст», — и потому о теме
/// не знает вовсе. Цвет выделения берётся системный: у человека он уже выбран, и навязывать
/// поверх него свой фирменный незачем.
/// </summary>
internal static class Theme
{
	// Запасной цвет выделения — на случай, если система своего не отдаёт.
	private static readonly Color _fallbackAccent = Color.FromRgb(0x4C, 0x6E, 0xF5);

	private static ResourceDictionary? _palette;

	public static ThemeMode Mode { get; private set; } = ThemeMode.Auto;

	/// <summary>Что показано сейчас — с учётом системной настройки, если выбрано «как в системе».</summary>
	public static bool Dark { get; private set; }

	/// <summary>Тема сменилась — то, что окрашено из кода, нужно перекрасить.</summary>
	public static event Action? Changed;

	public static void Initialize()
	{
		// Системную тему меняют и вручную, и по расписанию: следим, пока выбрано «как в системе».
		SystemEvents.UserPreferenceChanged += (_, e) =>
		{
			if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.Auto)
			{
				Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.Auto));
			}
		};

		// Стили грузятся один раз: они обращаются к ролям, а не к цветам, и подмену палитры
		// переживают без перезагрузки.
		Application.Current?.Resources.MergedDictionaries.Add(
			new ResourceDictionary { Source = new Uri("Ui/Themes/Styles.xaml", UriKind.Relative) });

		Apply(Store.Current.Theme);
	}

	public static void Apply(ThemeMode mode)
	{
		Mode = mode;
		Dark = mode switch
		{
			ThemeMode.Light => false,
			ThemeMode.Dark => true,
			_ => SystemPrefersDark(),
		};

		var palette = new ResourceDictionary
		{
			Source = new Uri($"Ui/Themes/Theme.{(Dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
		};

		Accentuate(palette);

		if (Application.Current is { } application)
		{
			if (_palette is not null)
			{
				application.Resources.MergedDictionaries.Remove(_palette);
			}

			application.Resources.MergedDictionaries.Add(palette);
		}

		_palette = palette;

		if (Store.Current.Theme != mode)
		{
			Store.Current.Theme = mode;
			Store.Save();
		}

		Changed?.Invoke();
	}

	/// <summary>Цвет выделения и то, что от него производно, кладём в тот же словарь.</summary>
	private static void Accentuate(ResourceDictionary palette)
	{
		var accent = SystemAccent() ?? _fallbackAccent;

		// На тёмном полотне системный цвет часто оказывается слишком плотным, на светлом —
		// слишком светлым. Сдвигаем его к читаемому, вместо того чтобы брать как есть.
		var tuned = Shift(accent, Dark ? 0.18 : -0.06);

		palette["Accent"] = new SolidColorBrush(tuned);
		palette["AccentHover"] = new SolidColorBrush(Shift(tuned, Dark ? 0.10 : 0.10));
		palette["AccentText"] = new SolidColorBrush(Luminance(tuned) > 0.6 ? Colors.Black : Colors.White);
		palette["AccentFaint"] = new SolidColorBrush(Color.FromArgb(Dark ? (byte)0x33 : (byte)0x22,
			tuned.R, tuned.G, tuned.B));
	}

	/// <summary>Цвет выделения из настроек Windows; null — система его не отдала.</summary>
	private static Color? SystemAccent()
	{
		// Значение лежит как AABBGGRR — порядок обратный привычному.
		if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null)
			is not int packed)
		{
			return null;
		}

		var value = unchecked((uint)packed);

		return Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
	}

	private static bool SystemPrefersDark() =>
		Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
			"AppsUseLightTheme", 1) is int light && light == 0;

	/// <summary>Осветлить (положительная доля) или затемнить цвет.</summary>
	private static Color Shift(Color color, double amount)
	{
		byte Move(byte channel) => (byte)Math.Clamp(
			amount >= 0 ? channel + (255 - channel) * amount : channel * (1 + amount), 0, 255);

		return Color.FromRgb(Move(color.R), Move(color.G), Move(color.B));
	}

	private static double Luminance(Color color) =>
		(0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
}
