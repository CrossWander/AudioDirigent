using System;
using System.Diagnostics;
using System.Text;

namespace AudioDirigent;

/// <summary>Запуск консольных утилит Windows: schtasks, pnputil, net, powershell.</summary>
internal static class Shell
{
	/// <summary>Вывод утилиты; при ошибке запуска — пустая строка.</summary>
	public static string Output(string program, params string[] arguments) => Run(program, arguments).Output;

	/// <summary>Код возврата утилиты; вывод отбрасывается. -1 — запустить не удалось.</summary>
	public static int Execute(string program, params string[] arguments) => Run(program, arguments).Exit;

	private static (int Exit, string Output) Run(string program, string[] arguments)
	{
		using var process = Start(program, arguments);
		if (process is null)
		{
			return (-1, "");
		}

		// Оба потока читаются одновременно: если ждать stdout до конца, утилита с длинным
		// stderr упрётся в переполненный буфер, а мы — в чтение, и оба встанут навсегда.
		var errors = process.StandardError.ReadToEndAsync();
		var output = process.StandardOutput.ReadToEnd();
		errors.GetAwaiter().GetResult();
		process.WaitForExit();

		return (process.ExitCode, output);
	}

	private static Process? Start(string program, string[] arguments)
	{
		var info = new ProcessStartInfo(program)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			// Утилиты пишут в кодировке консоли, а она у каждой системы своя. Нам нужны
			// только идентификаторы и латинские имена — Latin1 не портит ни одного байта.
			StandardOutputEncoding = Encoding.Latin1,
			StandardErrorEncoding = Encoding.Latin1,
		};

		foreach (var argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		try
		{
			return Process.Start(info);
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}
}
