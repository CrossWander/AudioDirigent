using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AudioDirigent;

/// <summary>Правила выбора устройства для одного направления: приоритеты и запреты.</summary>
internal sealed record Config(List<string> Priority, List<string> Blocked)
{
	/// <summary>Любая гарнитура HyperX; виртуальные устройства драйвера отсекаются списком запретов.</summary>
	public const string HeadsetPattern = "HyperX";

	public static bool Matches(AudioEndpoint device, string pattern) =>
		device.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

	/// <summary>Правил нет вовсе — направление не настроено, и трогать его незачем.</summary>
	[JsonIgnore]
	public bool Empty => Priority.Count == 0 && Blocked.Count == 0;

	/// <summary>Лучшее устройство по этим правилам; null — подходящих нет.</summary>
	public AudioEndpoint? SelectBest(IEnumerable<AudioEndpoint> active, bool headsetOff)
	{
		var candidates = active.Where(device => !Unusable(device, headsetOff)).ToList();

		return Priority
			.Select(pattern => candidates.FirstOrDefault(device => Matches(device, pattern)))
			.FirstOrDefault(device => device is not null);
	}

	/// <summary>Устройство — гарнитура HyperX, а не виртуальное устройство её драйвера.</summary>
	public bool IsHeadset(AudioEndpoint device) => Matches(device, HeadsetPattern) && !Blocks(device);

	/// <summary>Устройство запрещено правилами либо принадлежит выключенной гарнитуре.</summary>
	public bool Unusable(AudioEndpoint device, bool headsetOff) =>
		Blocked.Any(pattern => Matches(device, pattern))
		|| (headsetOff && Matches(device, HeadsetPattern));

	/// <summary>Правила, под которые попадает устройство: и точные имена, и общие паттерны.</summary>
	public IEnumerable<string> Rules(AudioEndpoint device) =>
		Priority.Concat(Blocked).Where(pattern => Matches(device, pattern));

	/// <summary>Место устройства в списке приоритетов; -1 — его там нет.</summary>
	public int Rank(AudioEndpoint device) => Priority.FindIndex(pattern => Matches(device, pattern));

	public bool Blocks(AudioEndpoint device) => Blocked.Any(pattern => Matches(device, pattern));

	/// <summary>В приоритеты. Точное имя добавляется, только если общий паттерн устройство ещё не ловит.</summary>
	public Config Prioritise(AudioEndpoint device)
	{
		var next = Unblock(device);
		if (next.Rank(device) < 0)
		{
			next.Priority.Add(device.Name);
		}

		return next;
	}

	/// <summary>В запреты. Приоритеты не трогаем: запрет и так сильнее, а общий паттерн нужен другим устройствам.</summary>
	public Config Block(AudioEndpoint device)
	{
		// Уже запрещено общим паттерном — добавлять точное имя незачем, а удалять
		// тот паттерн нельзя: он держит и остальные устройства драйвера.
		if (Blocks(device))
		{
			return this;
		}

		var next = Copy();
		next.Blocked.Add(device.Name);

		return next;
	}

	/// <summary>Убрать устройство из обоих списков.</summary>
	public Config Clear(AudioEndpoint device)
	{
		var next = Unblock(device);
		next.Priority.RemoveAll(pattern => Matches(device, pattern));

		return next;
	}

	/// <summary>Сдвинуть устройство по списку приоритетов; те же правила, если двигать некуда.</summary>
	public Config Move(AudioEndpoint device, int delta)
	{
		var next = Copy();
		var index = next.Rank(device);
		var target = index + delta;

		if (index < 0 || target < 0 || target >= next.Priority.Count)
		{
			return this;
		}

		(next.Priority[index], next.Priority[target]) = (next.Priority[target], next.Priority[index]);

		return next;
	}

	private Config Unblock(AudioEndpoint device)
	{
		var next = Copy();
		next.Blocked.RemoveAll(pattern => Matches(device, pattern));

		return next;
	}

	private Config Copy() => new([.. Priority], [.. Blocked]);
}
