using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace AudioDirigent;

/// <summary>Язык интерфейса: словарь строк подменяется целиком, окно перечитывает подписи.</summary>
internal static class Localization
{
	public static readonly string[] Codes = ["ru", "uk", "en"];

	private static ResourceDictionary? _strings;

	public static string Current { get; private set; } = "en";

	/// <summary>Язык сменился — подписи, заданные из кода, нужно перечитать.</summary>
	public static event Action? Changed;

	public static void Initialize()
	{
		// Программа портабельная: настройка живёт рядом с exe. Ранние версии писали её
		// в реестр — убираем след, чтобы после удаления папки в системе ничего не осталось.
		Registry.CurrentUser.DeleteSubKeyTree(@"Software\AudioDirigent", throwOnMissingSubKey: false);

		Apply(Store.Current.Language);
	}

	public static void Apply(string code)
	{
		if (!Codes.Contains(code))
		{
			code = "en";
		}

		var strings = new ResourceDictionary { Source = new Uri($"Lang/Lang.{code}.xaml", UriKind.Relative) };

		if (Application.Current is { } application)
		{
			if (_strings is not null)
			{
				application.Resources.MergedDictionaries.Remove(_strings);
			}

			application.Resources.MergedDictionaries.Add(strings);
		}

		_strings = strings;
		Current = code;

		if (Store.Current.Language != code)
		{
			Store.Current.Language = code;
			Store.Save();
		}

		Changed?.Invoke();
	}

	public static string Get(string key) => _strings?[key] as string ?? key;

	public static string Format(string key, params object?[] arguments) =>
		string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

	/// <summary>Развернуть сообщение, пришедшее снизу, в текст на текущем языке.</summary>
	public static string Of(Phrase phrase) => Format(phrase.Key, phrase.Arguments);

	/// <summary>Строка журнала со временем — так её показывают и в окне, и в консоли.</summary>
	public static string Of(LogEntry entry) => $"{entry.Time:HH:mm:ss} {Of(entry.Message)}";
}
