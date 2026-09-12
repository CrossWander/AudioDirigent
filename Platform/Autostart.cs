using Microsoft.Win32;
using System;
using System.IO;
using System.Security;
using System.Text;

namespace AudioDirigent;

/// <summary>Автозапуск через Планировщик заданий Windows — задача видна и правится в taskschd.msc.</summary>
internal static class Autostart
{
	public const string TaskName = "AudioDirigent";

	private const string _legacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

	// Ранние версии заводили вторую задачу на пробуждение; теперь оба триггера живут в одной.
	private const string _legacyWakeTaskName = TaskName + "-Wake";

	// Пробуждение из сна и гибернации: это событие система пишет каждый раз, когда вернулась.
	// Вход в систему при этом не повторяется, так что триггер входа тут молчит, а поднять
	// программу больше некому. Живой экземпляр отсечёт второй запуск мьютексом.
	private const string _wakeEvent =
		"*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]";

	public static bool IsEnabled() => Shell.Execute("schtasks.exe", "/Query", "/TN", TaskName) == 0;

	/// <summary>Задача ранних версий не запускается от батареи: schtasks завёл её с условиями
	/// по умолчанию. Сама она не починится, поэтому переписываем такую задачу молча.</summary>
	public static void Repair()
	{
		var task = Shell.Output("schtasks.exe", "/Query", "/TN", TaskName, "/XML");
		if (task.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase))
		{
			// Права задаче достались при создании; понижать их только потому,
			// что сейчас программа запущена обычным образом, не за что.
			Set(true, task.Contains("HighestAvailable", StringComparison.Ordinal));
		}
	}

	/// <summary>Возвращает текст ошибки либо null при успехе.</summary>
	public static string? Set(bool enabled) => Set(enabled, Recovery.Elevated);

	private static string? Set(bool enabled, bool elevated)
	{
		// Ранние версии прописывали автозапуск в реестр — убираем хвост, чтобы не стартовало дважды.
		using (var key = Registry.CurrentUser.OpenSubKey(_legacyRunKey, writable: true))
		{
			key?.DeleteValue(TaskName, throwOnMissingValue: false);
		}

		Shell.Execute("schtasks.exe", "/Delete", "/TN", _legacyWakeTaskName, "/F");

		if (!enabled)
		{
			var removed = Shell.Execute("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
			return removed == 0 || !IsEnabled() ? null : Localization.Format("langErrorSchtasks", "/Delete", removed);
		}

		// Задача описывается файлом, а не ключами командной строки: schtasks /Create заводит её
		// с условиями по умолчанию, а они для программы в трее губительны — на ноутбуке от
		// батареи задача не стартует вовсе, при отключении от розетки её убивают, и живёт она
		// не дольше трёх суток. Ни одно из трёх ключами не отключается.
		var file = Path.Combine(Path.GetTempPath(), $"{TaskName}.task.xml");
		try
		{
			File.WriteAllText(file, Describe(elevated), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
			var created = Shell.Execute("schtasks.exe", "/Create", "/TN", TaskName, "/XML", file, "/F");

			return created == 0 ? null : Localization.Format("langErrorSchtasks", "/Create", created);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Localization.Format("langErrorSchtasks", "/Create", exception.Message);
		}
		finally
		{
			try
			{
				File.Delete(file);
			}
			catch (IOException)
			{
			}
		}
	}

	private static string Describe(bool elevated)
	{
		var user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
		var command = SecurityElement.Escape(Environment.ProcessPath);

		// Восстановление звука переустанавливает устройства и перезапускает службу — без прав
		// администратора это не сделать. Задача сохранит те права, с которыми её создали.
		var level = elevated ? "HighestAvailable" : "LeastPrivilege";

		var subscription = SecurityElement.Escape(
			$"<QueryList><Query Id='0' Path='System'><Select Path='System'>{_wakeEvent}</Select></Query></QueryList>");

		return $"""
			<?xml version="1.0" encoding="UTF-16"?>
			<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
			  <RegistrationInfo>
			    <Description>Keeps the Windows audio output on a device that actually works.</Description>
			  </RegistrationInfo>
			  <Triggers>
			    <LogonTrigger>
			      <Enabled>true</Enabled>
			      <UserId>{user}</UserId>
			    </LogonTrigger>
			    <EventTrigger>
			      <Enabled>true</Enabled>
			      <Subscription>{subscription}</Subscription>
			    </EventTrigger>
			  </Triggers>
			  <Principals>
			    <Principal id="Author">
			      <UserId>{user}</UserId>
			      <LogonType>InteractiveToken</LogonType>
			      <RunLevel>{level}</RunLevel>
			    </Principal>
			  </Principals>
			  <Settings>
			    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
			    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
			    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
			    <IdleSettings>
			      <StopOnIdleEnd>false</StopOnIdleEnd>
			      <RestartOnIdle>false</RestartOnIdle>
			    </IdleSettings>
			    <RunOnlyIfIdle>false</RunOnlyIfIdle>
			    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
			    <StartWhenAvailable>true</StartWhenAvailable>
			    <AllowStartOnDemand>true</AllowStartOnDemand>
			    <AllowHardTerminate>true</AllowHardTerminate>
			    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
			    <Enabled>true</Enabled>
			    <Hidden>false</Hidden>
			    <WakeToRun>false</WakeToRun>
			    <Priority>7</Priority>
			  </Settings>
			  <Actions Context="Author">
			    <Exec>
			      <Command>{command}</Command>
			      <Arguments>--tray</Arguments>
			    </Exec>
			  </Actions>
			</Task>
			""";
	}
}
