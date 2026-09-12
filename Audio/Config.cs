using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AudioDirigent;

/// <summary>Правила выбора устройства для одного направления: приоритеты и запреты.</summary>
internal sealed record Config(List<string> Priority, List<string> Blocked)
{
	public static bool Matches(AudioEndpoint device, string pattern) =>
		device.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

	/// <summary>Правил нет вовсе — направление не настроено, и трогать его незачем.</summary>
	[JsonIgnore]
	public bool Empty => Priority.Count == 0 && Blocked.Count == 0;

	/// <summary>Лучшее устройство по этим правилам; null — подходящих нет.</summary>
	public AudioEndpoint? SelectBest(IEnumerable<AudioEndpoint> active)
	{
		var candidates = active.Where(device => !Blocks(device)).ToList();

		return Priority
			.Select(pattern => candidates.Where(device => Matches(device, pattern)).OrderBy(Preference).FirstOrDefault())
			.FirstOrDefault(device => device is not null);
	}

	/// <summary>
	/// Какое из устройств, попавших под один и тот же приоритет, брать первым. Гарнитура
	/// Bluetooth приходит в систему дважды: музыкой и телефонным профилем — моно, 16 кГц.
	/// Правило одно на оба направления: стоит сделать умолчанием телефонный микрофон, и
	/// Windows уводит туда же выход, после чего музыка звучит как телефонный звонок.
	/// Виртуальные устройства отодвигаются следом за ним, но не запрещаются: у того, кто
	/// поставил микшер, звук через микшер и идёт.
	/// </summary>
	private static int Preference(AudioEndpoint device) => device switch
	{
		{ HandsFree: true } => 2,
		{ Software: true } => 1,
		_ => 0,
	};

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

	/// <summary>
	/// В начало приоритетов — «сделать основным». Общий паттерн, который ловил устройство
	/// раньше, снимается: иначе он остался бы выше и продолжал решать за новую строку.
	/// </summary>
	public Config Promote(AudioEndpoint device)
	{
		var next = Unblock(device);
		next.Priority.RemoveAll(pattern => Matches(device, pattern));
		next.Priority.Insert(0, device.Name);

		return next;
	}

	/// <summary>В запреты. Приоритеты не трогаем: запрет и так сильнее, а общий паттерн нужен другим устройствам.</summary>
	public Config Block(AudioEndpoint device)
	{
		// Уже запрещено общим паттерном — добавлять точное имя незачем, а удалять
		// тот паттерн нельзя: он держит и остальные устройства того же драйвера.
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
