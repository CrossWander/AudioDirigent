using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AudioDirigent;

/// <summary>
/// Всё, что программа помнит между запусками, в одном config.json рядом с exe. Раньше
/// каждая настройка держала собственный текстовый файл, и папка обрастала ими.
/// </summary>
internal static class Store
{
	/// <summary>Содержимое файла. Правила направления null, пока их не настраивали: пустые — уже решение.</summary>
	internal sealed class Data
	{
		public string Language { get; set; } = "en";

		public bool Notify { get; set; } = true;

		/// <summary>Карточка подключённого устройства над треем.</summary>
		public bool Popup { get; set; } = true;

		public bool Hotkeys { get; set; }

		public string PauseHotkey { get; set; } = "Ctrl+Alt+P";

		public string RecoverHotkey { get; set; } = "Ctrl+Alt+R";

		public Config? Output { get; set; }

		public Config? Input { get; set; }

		public List<VolumeRule> Volume { get; set; } = [];

		/// <summary>Запрет на отключение питания портов: сам переключатель и узлы, с которых он снят нами.</summary>
		public bool UsbPower { get; set; }

		public List<string> UsbPowerHeld { get; set; } = [];

		/// <summary>Опрос приёмника беспроводной гарнитуры; null — не настроен, и программа его не ведёт.</summary>
		public ProbeRule? Probe { get; set; }
	}

	private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "config.json");

	// Файл открывает и правит человек: с отступами, кириллицей вместо \uXXXX и терпимо
	// к комментариям и лишней запятой в конце списка.
	private static readonly JsonSerializerOptions _format = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static Data Current { get; } = Read();

	/// <summary>Записать файл; false — папка только для чтения, настройка не переживёт перезапуск.</summary>
	public static bool Save() => Write(Current);

	/// <summary>Копия через формат файла: проверка сборки ловит поломку контракта JSON.</summary>
	public static Data? Roundtrip(Data data) =>
		JsonSerializer.Deserialize<Data>(JsonSerializer.Serialize(data, _format), _format);

	private static bool Write(Data data)
	{
		try
		{
			File.WriteAllText(_path, JsonSerializer.Serialize(data, _format), new UTF8Encoding(true));

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static Data Read()
	{
		try
		{
			if (File.Exists(_path))
			{
				return JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), _format) ?? new Data();
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Файл испорчен или недоступен. Молчать нельзя: без записи в журнале это
			// выглядит как самовольно забытые правила.
			Journal.Add("langLogFatal", exception.Message);

			return new Data();
		}

		return new Data();
	}
}
