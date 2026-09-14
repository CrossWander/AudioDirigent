using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AudioDirigent;

/// <summary>Чем закончился вопрос к GitHub.</summary>
internal enum UpdateState
{
	Latest,
	Newer,
	Failed,
}

/// <summary>Что известно о последнем релизе.</summary>
/// <param name="Download">Прямая ссылка на exe; null — в релизе его нет, ставить нечего.</param>
internal sealed record Release(UpdateState State, string? Version, string? Download);

/// <summary>
/// Новые версии на GitHub: проверка, загрузка и подмена себя. Сеть — единственное, ради чего
/// программа вообще выходит наружу, поэтому по умолчанию она туда не ходит, пока не попросят.
/// </summary>
internal static class Updates
{
	public const string Releases = "https://github.com/CrossWander/AudioDirigent/releases";

	private const string _api = "https://api.github.com/repos/CrossWander/AudioDirigent/releases/latest";

	// Ежедневная проверка — на случай, когда её включили: раз в сутки от прошлой удачной.
	private static readonly TimeSpan _period = TimeSpan.FromDays(1);
	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan _download = TimeSpan.FromMinutes(5);

	/// <summary>Пора ли спрашивать GitHub: включена ежедневная проверка и сутки прошли.</summary>
	public static bool Due =>
		Store.Current.AutoUpdate
		&& (Store.Current.UpdateChecked is not { } last || DateTime.UtcNow - last > _period);

	/// <summary>Спросить GitHub о последнем релизе.</summary>
	public static async Task<Release> Check()
	{
		try
		{
			using var client = Client();
			var response = await client.GetAsync(_api);

			// Релизов нет вовсе — это 404. Скачивать нечего, значит текущая версия и есть последняя.
			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return new Release(UpdateState.Latest, null, null);
			}

			if (!response.IsSuccessStatusCode)
			{
				return new Release(UpdateState.Failed, null, null);
			}

			var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
			var tag = release.GetProperty("tag_name").GetString();

			// Тег не разобрать — сравнить не с чем, и молчать об этом нельзя: для пользователя
			// это такой же провал проверки, как обрыв сети.
			if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var latest)
				|| !Version.TryParse(Build.Version, out var current))
			{
				return new Release(UpdateState.Failed, null, null);
			}

			Remember();

			return latest > current
				? new Release(UpdateState.Newer, latest.ToString(3), Executable(release))
				: new Release(UpdateState.Latest, null, null);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
			or JsonException or KeyNotFoundException or InvalidOperationException)
		{
			return new Release(UpdateState.Failed, null, null);
		}
	}

	/// <summary>
	/// Скачать новую версию и заменить себя ею. Возвращает текст ошибки; при удаче не
	/// возвращается вовсе — программа к этому моменту уже закрывается.
	/// </summary>
	public static async Task<string?> Install(Release release, Action shutdown)
	{
		if (release.Download is not { } url || Environment.ProcessPath is not { } self)
		{
			return Localization.Get("langUpdateNoAsset");
		}

		var fresh = self + ".new";
		try
		{
			using (var client = Client())
			{
				client.Timeout = _download;
				var bytes = await client.GetByteArrayAsync(url);

				// Пустой или подозрительно маленький ответ — это не программа, а страница ошибки.
				if (bytes.Length < 1_000_000)
				{
					return Localization.Get("langUpdateBadDownload");
				}

				await File.WriteAllBytesAsync(fresh, bytes);
			}

			// Собственный файл занят, пока процесс жив, — подменить его может только тот, кто
			// дождётся выхода. Этим и занимается короткий сценарий, который заодно убирает
			// за собой: и скачанный файл, и себя самого.
			Handover(self, fresh);
			shutdown();

			return null;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
			or IOException or UnauthorizedAccessException)
		{
			Forget(fresh);

			// Чаще всего это папка, куда писать не дают: программу положили в Program Files.
			return exception is UnauthorizedAccessException
				? Localization.Get("langUpdateDenied")
				: Localization.Format("langUpdateFailedWith", exception.Message);
		}
	}

	private static HttpClient Client()
	{
		var client = new HttpClient { Timeout = _timeout };

		// GitHub отвечает 403 на запрос без User-Agent.
		client.DefaultRequestHeaders.Add("User-Agent", "AudioDirigent");

		return client;
	}

	/// <summary>Ссылка на exe среди файлов релиза; null — его там нет.</summary>
	private static string? Executable(JsonElement release)
	{
		if (!release.TryGetProperty("assets", out var assets))
		{
			return null;
		}

		foreach (var asset in assets.EnumerateArray())
		{
			if (asset.TryGetProperty("name", out var name)
				&& name.GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
				&& asset.TryGetProperty("browser_download_url", out var url))
			{
				return url.GetString();
			}
		}

		return null;
	}

	/// <summary>
	/// Оставить сценарий, который дождётся выхода программы, подменит файл и запустит новую
	/// версию. Ждёт по факту освобождения файла, а не по таймеру: одиночный exe распаковывает
	/// себя сам, и после выхода процесса файл занят ещё секунду.
	/// </summary>
	private static void Handover(string self, string fresh)
	{
		var script = Path.Combine(Path.GetTempPath(), $"audiodirigent-update-{Environment.ProcessId}.cmd");
		var text = $"""
			@echo off
			set "target={self}"
			set "fresh={fresh}"
			for /l %%i in (1,1,40) do (
			  move /y "%fresh%" "%target%" >nul 2>&1 && goto started
			  ping -n 2 127.0.0.1 >nul
			)
			del "%fresh%" >nul 2>&1
			goto done
			:started
			start "" "%target%"
			:done
			del "%~f0" >nul 2>&1

			""";

		File.WriteAllText(script, text, new UTF8Encoding(false));

		Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			WorkingDirectory = Path.GetTempPath(),
		});
	}

	private static void Forget(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Файла нет или он занят — убирать больше нечего.
		}
	}

	private static void Remember()
	{
		Store.Current.UpdateChecked = DateTime.UtcNow;
		Store.Save();
	}
}
