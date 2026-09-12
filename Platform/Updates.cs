using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

/// <summary>
/// Проверка новой версии по релизам GitHub. Единственный выход программы в сеть, поэтому
/// он делается только по нажатию кнопки в настройках.
/// </summary>
internal static class Updates
{
	public const string Releases = "https://github.com/CrossWander/AudioDirigent/releases";

	private const string _api = "https://api.github.com/repos/CrossWander/AudioDirigent/releases/latest";

	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

	/// <summary>Спросить GitHub о последнем релизе; версия заполнена только у <see cref="UpdateState.Newer"/>.</summary>
	public static async Task<(UpdateState State, string? Version)> Check()
	{
		try
		{
			using var client = new HttpClient { Timeout = _timeout };

			// GitHub отвечает 403 на запрос без User-Agent.
			client.DefaultRequestHeaders.Add("User-Agent", "AudioDirigent");

			var response = await client.GetAsync(_api);

			// Релизов нет вовсе — это 404. Скачивать нечего, значит текущая версия и есть последняя.
			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return (UpdateState.Latest, null);
			}

			if (!response.IsSuccessStatusCode)
			{
				return (UpdateState.Failed, null);
			}

			var tag = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
				.RootElement.GetProperty("tag_name").GetString();

			// Тег не разобрать — сравнить не с чем, и молчать об этом нельзя: для пользователя
			// это такой же провал проверки, как обрыв сети.
			if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var latest)
				|| !Version.TryParse(Build.Version, out var current))
			{
				return (UpdateState.Failed, null);
			}

			return latest > current
				? (UpdateState.Newer, latest.ToString(3))
				: (UpdateState.Latest, null);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
			or JsonException or KeyNotFoundException or InvalidOperationException)
		{
			return (UpdateState.Failed, null);
		}
	}
}
