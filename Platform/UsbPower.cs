using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioDirigent;

/// <summary>
/// Галка «Разрешить отключение этого устройства для экономии энергии». Она стоит по умолчанию
/// на USB-концентраторах, и из-за неё устройство после гибернации иногда не оживает вовсе.
/// Переключатель снимает её со всех узлов USB разом и возвращает обратно ровно то, что снял.
/// </summary>
internal static class UsbPower
{
	public static bool Enabled => Store.Current.UsbPower;

	/// <summary>Включить или выключить запрет на отключение питания портов. Текст ошибки либо null.</summary>
	public static string? Set(bool enabled, Action<string, object?[]> log)
	{
		if (!Recovery.Elevated)
		{
			return Localization.Get("langErrorNeedsAdmin");
		}

		try
		{
			if (!enabled)
			{
				Restore(log);
				return null;
			}

			// Берём только те узлы, где отключение сейчас разрешено: остальные кто-то
			// выключил до нас, и возвращать их потом было бы самоуправством.
			var take = PowerManaged().Where(device => device.Enable).Select(device => device.Name).ToList();

			// Список пишется до того, как что-то изменено: не запишется — не изменим ничего.
			// Иначе на носителе только для чтения галки были бы сняты, а вернуть их нечем.
			if (!Remember(on: true, take))
			{
				Remember(on: false, []);

				return Localization.Get("langErrorConfigWrite");
			}

			if (take.Count > 0)
			{
				Switch(off: take, on: []);
				log("langLogUsbHeld", [take.Count]);
			}

			return null;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}
	}

	private static void Restore(Action<string, object?[]> log)
	{
		if (Store.Current.UsbPowerHeld is { Count: > 0 } remembered)
		{
			Switch(off: [], on: remembered);
			log("langLogUsbReleased", [remembered.Count]);
		}

		Remember(on: false, []);
	}

	/// <summary>Узлы USB, у которых Windows показывает галку отключения питания, и её состояние.</summary>
	private static List<(string Name, bool Enable)> PowerManaged() =>
	[
		.. PowerShell("Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable"
				+ " | ForEach-Object { \"$($_.Enable) $($_.InstanceName)\" }")
			.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Contains(' '))
			.Select(line => (
				Name: line[(line.IndexOf(' ') + 1)..],
				Enable: line.StartsWith("True", StringComparison.OrdinalIgnoreCase)))
			// Только шина USB: та же галка есть у сетевых карт и чипсета, и туда лезть незачем.
			.Where(device => device.Name.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)),
	];

	private static void Switch(IEnumerable<string> off, IEnumerable<string> on)
	{
		// WMI отдаёт эти объекты только целой выборкой, поэтому оба списка уходят одним вызовом.
		PowerShell($"$off=@({Quote(off)});$on=@({Quote(on)});"
			+ "Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable | ForEach-Object {"
			+ "if ($off -contains $_.InstanceName) { $_.Enable = $false; Set-CimInstance -InputObject $_ }"
			+ "elseif ($on -contains $_.InstanceName) { $_.Enable = $true; Set-CimInstance -InputObject $_ } }");
	}

	private static string PowerShell(string script) =>
		Shell.Output("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);

	// В идентификаторах узлов апострофа нет, так что одинарные кавычки достаточно надёжны.
	private static string Quote(IEnumerable<string> names) =>
		string.Join(',', names.Select(name => $"'{name}'"));

	// Отказ записи здесь глотать нельзя: список — единственный след того, что мы изменили.
	private static bool Remember(bool on, List<string> held)
	{
		Store.Current.UsbPower = on;
		Store.Current.UsbPowerHeld = held;

		return Store.Save();
	}
}
