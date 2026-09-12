using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;

namespace AudioDirigent;

/// <summary>
/// Замена перезагрузке. После гибернации устройство иногда не возвращается вовсе: узлы PnP
/// на месте, конечных точек звука нет, и переткнуть кабель не помогает. Делаем то же, что
/// система делает при старте: переустанавливаем пропавшие устройства, а если и это не
/// помогло — перезапускаем построитель конечных точек звука.
/// </summary>
internal static partial class Recovery
{
	// Устройство возвращается в список не мгновенно: переустановка идёт через PnP,
	// а служба звука пересобирает конечные точки уже после своего старта.
	private static readonly TimeSpan _settle = TimeSpan.FromSeconds(5);

	/// <summary>И переустановка устройства, и перезапуск службы звука доступны только администратору.</summary>
	public static bool Elevated =>
		new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

	/// <summary>
	/// Вернуть устройства в систему. <paramref name="nodes"/> — узлы PnP тех, что пропали;
	/// <paramref name="restored"/> — проверка, что перечисленные ей узлы уже вернулись.
	/// Спрашивают не про весь список, а про то, что действительно удалось перезапустить:
	/// устройство, которого нет и в PnP, не вернётся и от перезапуска службы.
	/// </summary>
	public static void Run(Action<string, object?[]> log, List<string> nodes, Func<IReadOnlyCollection<string>, bool> restored)
	{
		if (!Elevated)
		{
			log("langLogRecoveryDenied", []);
			return;
		}

		var targets = Targets(nodes);
		if (Restart(log, targets) && restored(targets))
		{
			log("langLogRecoveryOk", []);
			return;
		}

		// Построитель конечных точек держит весь список устройств; его перезапуск пересобирает
		// список заново — ради этого обычно и перезагружают систему. Останов уводит и саму
		// службу звука, поэтому поднимаем её, а построитель идёт следом.
		log("langLogRecoveryService", []);
		Shell.Execute("net.exe", "stop", "AudioEndpointBuilder", "/y");
		Shell.Execute("net.exe", "start", "Audiosrv");

		Thread.Sleep(_settle);
		log(restored(targets) ? "langLogRecoveryOk" : "langLogRecoveryFailed", []);
	}

	/// <summary>Переустановить устройства; false — переустанавливать было нечего.</summary>
	private static bool Restart(Action<string, object?[]> log, List<string> targets)
	{
		if (targets.Count == 0)
		{
			return false;
		}

		log("langLogRecoveryDevices", [string.Join(", ", targets)]);
		foreach (var node in targets)
		{
			Shell.Execute("pnputil.exe", "/restart-device", node);
		}

		Thread.Sleep(_settle);

		return true;
	}

	/// <summary>
	/// Что перезапускать: для каждого пропавшего устройства — его составной родитель.
	/// Узлы, которых система уже не перечисляет, отбрасываются: устройство, вынутое из
	/// разъёма, перезапускать нечем, и звать pnputil по такому узлу бессмысленно.
	/// </summary>
	private static List<string> Targets(List<string> nodes)
	{
		if (nodes.Count == 0)
		{
			return [];
		}

		var all = Known();
		var present = all.ToHashSet(StringComparer.OrdinalIgnoreCase);

		return
		[
			.. nodes
				.Where(present.Contains)
				.Select(node => Parent(node, all))
				.Distinct(StringComparer.OrdinalIgnoreCase)
		];
	}

	/// <summary>Все узлы PnP, которые система сейчас перечисляет.</summary>
	private static List<string> Known()
	{
		var all = new List<string>();

		// Пустая строка разделяет устройства. Подписи полей переведены на язык системы,
		// а идентификатор устройства — нет, по нему и ищем. Только физические и корневые
		// узлы: конечная точка звука (SWD) переустановке не поддаётся.
		foreach (var line in Shell.Output("pnputil.exe", "/enum-devices").Split('\n'))
		{
			if (InstanceId().Match(line.Trim()) is { Success: true } match)
			{
				all.Add(match.Value);
			}
		}

		return all;
	}

	/// <summary>Составное USB-устройство, которому принадлежит узел, или сам узел, если родителя нет.</summary>
	// Звук гарнитуры сидит на одном интерфейсе составного устройства (MI_00), а её же
	// управляющий HID — на другом (MI_03). Перезапуск одного интерфейса оставлял второй
	// в прежнем состоянии: звук возвращался, а приложение производителя продолжало писать,
	// что гарнитуры нет. Родитель поднимает все интерфейсы разом; это и есть передёргивание
	// кабеля, только без кабеля.
	internal static string Parent(string node, List<string> all) =>
		Composite().Match(node) is { Success: true } match
		&& all.FirstOrDefault(other =>
			other.StartsWith($@"USB\{match.Groups[1].Value}\", StringComparison.OrdinalIgnoreCase)) is { } parent
			? parent
			: node;

	[GeneratedRegex(@"(?:USB|ROOT)\\\S+")]
	private static partial Regex InstanceId();

	[GeneratedRegex(@"^USB\\(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})&MI_", RegexOptions.IgnoreCase)]
	private static partial Regex Composite();
}
