using System.Collections.Generic;
using System.Linq;

namespace AudioDirigent;

/// <summary>Правила обоих направлений: вывод и запись настраиваются порознь.</summary>
internal sealed record Settings(Config Output, Config Input)
{
	public Config For(EDataFlow flow) => flow == EDataFlow.Capture ? Input : Output;

	public Settings With(EDataFlow flow, Config config) =>
		flow == EDataFlow.Capture ? this with { Input = config } : this with { Output = config };

	public static Settings Load()
	{
		// Правила null — их ещё не настраивали; пустые списки, наоборот, решение пользователя.
		var settings = new Settings(
			Store.Current.Output ?? Defaults(EDataFlow.Render),
			Store.Current.Input ?? Defaults(EDataFlow.Capture));

		if (Store.Current.Output is null || Store.Current.Input is null)
		{
			settings.Save();
		}

		return settings;
	}

	public void Save()
	{
		Store.Current.Output = Output;
		Store.Current.Input = Input;
		Store.Save();
	}

	/// <summary>
	/// Умолчания первого запуска — одна строка: то устройство, которое стоит по умолчанию
	/// сейчас. Программа из коробки делает ровно одно — держит уже сделанный выбор и
	/// возвращает его, если что-то попыталось перехватить. Всё остальное в этом списке
	/// появится решением человека, а не догадкой программы. Запреты пусты по той же причине.
	/// </summary>
	private static Config Defaults(EDataFlow flow)
	{
		List<string> priority = [];
		if (Fallback(flow) is { } device)
		{
			priority.Add(device);
		}

		return new(priority, []);
	}

	private static string? Fallback(EDataFlow flow)
	{
		// Устройство по умолчанию к первому запуску уже может быть перехвачено виртуальным
		// драйвером или телефонным профилем Bluetooth — такое запоминать бессмысленно,
		// берём первое живое устройство, которое играет всерьёз.
		var role = flow == EDataFlow.Capture ? ERole.Communications : ERole.Multimedia;

		return (Audio.GetDefault(flow, role) is { HandsFree: false, Software: false } current
			? current
			: Audio.ListDevices(flow, DeviceState.Active)
				.FirstOrDefault(device => device is { HandsFree: false, Software: false }))?.Name;
	}
}
