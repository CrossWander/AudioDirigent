using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;

namespace AudioDirigent;

/// <summary>
/// Замена перезагрузке. После гибернации гарнитура иногда не возвращается вовсе:
/// узлы PnP на месте, конечных точек звука нет, и переткнуть кабель не помогает.
/// Делаем то же, что система делает при старте: снимаем NGENUITY, переустанавливаем
/// устройства HyperX, при необходимости перезапускаем построитель конечных точек звука
/// и поднимаем NGENUITY обратно.
/// </summary>
internal static partial class Recovery
{
	// Устройство возвращается в список не мгновенно: переустановка идёт через PnP,
	// а служба звука пересобирает конечные точки уже после своего старта.
	private static readonly TimeSpan _settle = TimeSpan.FromSeconds(5);

	// NGENUITY стартует не быстро, а окно рисует ещё позже.
	private static readonly TimeSpan _ngenuityStart = TimeSpan.FromSeconds(40);
	private static readonly TimeSpan _ngenuityPoll = TimeSpan.FromSeconds(1);
	private const int _killTimeout = 5000;
	private const string _ngenuity = "NGENUITY";

	/// <summary>И переустановка устройства, и перезапуск службы звука доступны только администратору.</summary>
	public static bool Elevated =>
		new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

	/// <summary>NGENUITY запущен.</summary>
	// Пока его нет, «перезапустить и его» — то же самое, что обычная переустановка драйверов,
	// и выбор из двух пунктов только сбивает с толку.
	public static bool NgenuityRunning
	{
		get
		{
			var session = Process.GetCurrentProcess().SessionId;
			var running = false;

			foreach (var process in Process.GetProcessesByName(_ngenuity))
			{
				using (process)
				{
					running |= process.SessionId == session;
				}
			}

			return running;
		}
	}

	/// <summary>
	/// Вернуть гарнитуру в систему. <paramref name="present"/> — проверка, что она уже вернулась.
	/// <paramref name="withNgenuity"/> — снять NGENUITY на время починки и поднять обратно.
	/// </summary>
	public static void Run(Action<string, object?[]> log, Func<bool> present, bool withNgenuity)
	{
		if (!Elevated)
		{
			log("langLogRecoveryDenied", []);
			return;
		}

		// NGENUITY держит виртуальный аудиодрайвер открытым, а мы этот драйвер сейчас
		// переустановим. Снимаем клиента — так же, как это делает диспетчер задач, —
		// и поднимаем обратно, чем бы починка ни закончилась.
		var ngenuity = withNgenuity ? StopNgenuity(log) : null;
		try
		{
			Reload(log, present);
		}
		finally
		{
			StartNgenuity(ngenuity, log);
		}
	}

	private static void Reload(Action<string, object?[]> log, Func<bool> present)
	{
		if (Nodes(Config.HeadsetPattern) is { Count: > 0 } nodes)
		{
			log("langLogRecoveryDevices", [string.Join(", ", nodes)]);
			foreach (var node in nodes)
			{
				Shell.Execute("pnputil.exe", "/restart-device", node);
			}

			Thread.Sleep(_settle);
			if (present())
			{
				log("langLogRecoveryOk", []);
				return;
			}
		}

		// Построитель конечных точек держит весь список устройств вывода; его перезапуск
		// пересобирает список заново — ради этого обычно и перезагружают систему.
		// Останов уводит и саму службу звука, поэтому поднимаем её, а построитель идёт следом.
		log("langLogRecoveryService", []);
		Shell.Execute("net.exe", "stop", "AudioEndpointBuilder", "/y");
		Shell.Execute("net.exe", "start", "Audiosrv");

		Thread.Sleep(_settle);
		log(present() ? "langLogRecoveryOk" : "langLogRecoveryFailed", []);
	}

	/// <summary>Снять NGENUITY и вернуть путь к нему; null — он и не был запущен.</summary>
	private static string? StopNgenuity(Action<string, object?[]> log)
	{
		string? path = null;
		var session = Process.GetCurrentProcess().SessionId;

		foreach (var process in Process.GetProcessesByName(_ngenuity).Where(process => process.SessionId == session))
		{
			using (process)
			{
				path ??= Executable(process);
				try
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit(_killTimeout);
				}
				catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
				{
					// закрылся сам, пока мы до него шли
				}
			}
		}

		if (path is not null)
		{
			log("langLogNgenuityStopped", []);
		}

		return path;
	}

	private static void StartNgenuity(string? path, Action<string, object?[]> log)
	{
		if (path is null)
		{
			return;
		}

		// Через проводник: починка идёт с правами администратора, а NGENUITY должен
		// вернуться таким же, каким был, — обычным процессом пользователя.
		Shell.Execute("explorer.exe", path);

		log(ToTray() ? "langLogNgenuityStarted" : "langLogNgenuityWindow", []);
	}

	/// <summary>Ключа «запуститься свёрнутым» у NGENUITY нет, зато закрытие окна уводит его
	/// в трей и процесс остаётся жив. Дожидаемся окна и закрываем, как сделал бы человек.</summary>
	private static bool ToTray()
	{
		var deadline = DateTime.UtcNow + _ngenuityStart;
		while (DateTime.UtcNow < deadline)
		{
			foreach (var process in Process.GetProcessesByName(_ngenuity))
			{
				using (process)
				{
					if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
					{
						return true;
					}
				}
			}

			Thread.Sleep(_ngenuityPoll);
		}

		return false;
	}

	private static string? Executable(Process process)
	{
		try
		{
			return process.MainModule?.FileName;
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>Узлы PnP, в описании которых есть pattern: сама гарнитура и виртуальные устройства NGENUITY.</summary>
	private static List<string> Nodes(string pattern)
	{
		var all = new List<string>();
		var found = new List<string>();
		var id = "";
		var matched = false;

		// Пустая строка разделяет устройства. Подписи полей переведены на язык системы,
		// а идентификатор и имя устройства — нет, по ним и ищем.
		foreach (var line in Shell.Output("pnputil.exe", "/enum-devices").Split('\n').Append(""))
		{
			var text = line.Trim();
			if (text.Length == 0)
			{
				if (id.Length > 0)
				{
					all.Add(id);

					if (matched)
					{
						found.Add(id);
					}
				}

				(id, matched) = ("", false);
				continue;
			}

			// Только физические и корневые узлы: конечная точка звука (SWD) переустановке
			// не поддаётся, а имя гарнитуры стоит и в ней.
			if (InstanceId().Match(text) is { Success: true } match)
			{
				id = match.Value;
			}

			matched |= text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
		}

		return found.Select(node => Parent(node, all)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>Составное USB-устройство, которому принадлежит узел, или сам узел, если родителя нет.</summary>
	// Имя «HyperX» стоит только на звуковом интерфейсе гарнитуры (MI_00), а NGENUITY ищет её
	// через HID-интерфейс того же устройства (MI_03) — его перезапуск обходил стороной: звук
	// возвращался, а NGENUITY продолжал писать, что гарнитура не найдена. Родитель поднимает
	// все интерфейсы разом; это и есть передёргивание кабеля, только без кабеля.
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
