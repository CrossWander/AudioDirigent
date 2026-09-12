using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioDirigent;

/// <summary>
/// Запись журнала: хранится ключом с аргументами, текст собирается при показе.
/// </summary>
internal sealed record LogEntry(DateTime Time, string Key, string[] Arguments)
{
	/// <summary>Текст записи на языке интерфейса.</summary>
	public string Text => Localization.Format(Key, Arguments);

	public string Render() => $"{Time:HH:mm:ss} {Text}";
}

/// <summary>
/// Файл журнала рядом с программой. Записи хранятся ключом и аргументами, а не готовым
/// текстом: иначе после смены языка старые строки остались бы на прежнем.
/// </summary>
internal static class Journal
{
	// Строка файла: отметка времени, ключ и аргументы через табуляцию.
	private const string _timeFormat = "yyyy-MM-dd HH:mm:ss";

	// Журнал отвечает на вопрос «что случилось со звуком ночью», а не «что было в прошлом
	// месяце»: старше двух суток не хранится ничего, и длина всё равно ограничена.
	private const int _keepDays = 2;
	private const int _keepLines = 500;
	private const long _sizeLimit = 1_000_000;

	private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "log.txt");

	/// <summary>
	/// Записать событие и вернуть его — вызывающему обычно нужно показать его сразу.
	/// </summary>
	public static LogEntry Add(string key, params object?[] arguments)
	{
		var entry = new LogEntry(DateTime.Now, key, [.. arguments.Select(argument => argument?.ToString() ?? "")]);
		Append(entry);

		return entry;
	}

	/// <summary>Выбросить из файла всё старое: зовётся один раз, при запуске.</summary>
	public static void Trim()
	{
		try
		{
			if (!File.Exists(_path))
			{
				return;
			}

			var since = DateTime.Now.AddDays(-_keepDays);
			var lines = File.ReadAllLines(_path);
			var kept = lines
				.Where(line => Parse(line) is { } entry && entry.Time >= since)
				.TakeLast(_keepLines)
				.ToList();

			if (kept.Count != lines.Length)
			{
				File.WriteAllLines(_path, kept, new UTF8Encoding(true));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// журнал — не повод падать, а папка может быть доступна только на чтение
		}
	}

	/// <summary>
	/// Последние записи из файла — окно открывается позже старта и иначе пустое.
	/// </summary>
	public static IEnumerable<LogEntry> Recent(int lines)
	{
		try
		{
			return File.Exists(_path)
				? [.. File.ReadLines(_path).TakeLast(lines).Select(Parse).OfType<LogEntry>()]
				: [];
		}
		catch (IOException)
		{
			return [];
		}
	}

	private static void Append(LogEntry entry)
	{
		try
		{
			// Разросся за один сеанс — обрезаем, а не удаляем целиком: событие, из-за
			// которого журнал распух, обычно и есть самое интересное в нём.
			if (File.Exists(_path) && new FileInfo(_path).Length > _sizeLimit)
			{
				Trim();
			}

			var line = string.Join('\t', [entry.Time.ToString(_timeFormat, CultureInfo.InvariantCulture), entry.Key, .. entry.Arguments]);
			File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(true));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// журнал — не повод падать, а папка может быть доступна только на чтение
		}
	}

	private static LogEntry? Parse(string line)
	{
		var parts = line.TrimStart('\uFEFF').Split('\t');

		return parts.Length >= 2
			&& DateTime.TryParseExact(parts[0], _timeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
				? new LogEntry(time, parts[1], [.. parts.Skip(2)])
				: null;
	}
}
