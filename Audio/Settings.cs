using System.Collections.Generic;
using System.Linq;

namespace AudioDirigent;

/// <summary>Правила обоих направлений: вывод и запись настраиваются порознь.</summary>
internal sealed record Settings(Config Output, Config Input)
{
	// В запасной вариант не годятся ни виртуальные устройства драйвера, ни моно-канал
	// Bluetooth, ни сама гарнитура: она и так стоит первой строкой приоритетов.
	private static readonly Config _neverFallback =
		new([], ["NGENUITY", "HyperX Virtual Audio Device", "Hands-Free", Config.HeadsetPattern]);

	public Config For(EDataFlow flow) => flow == EDataFlow.Capture ? Input : Output;

	/// <summary>
	/// Гарнитура на месте целиком: и наушники, и микрофон, если он у неё есть. Микрофон
	/// сидит на том же узле USB и пропадает вместе с ним — но пропадать он умеет и один,
	/// а раньше такую поломку не замечали вовсе: наушники-то работают.
	/// </summary>
	public bool HeadsetPresent()
	{
		bool Live(EDataFlow flow) => Audio.ListDevices(flow, DeviceState.Active).Any(For(flow).IsHeadset);

		// Есть ли у гарнитуры микрофон, помнит сама Windows: она держит эндпоинт в списке
		// и после отключения. Выключенный вручную не считается — иначе починка гонялась бы
		// за микрофоном, которого владелец сам не хочет.
		bool Known(EDataFlow flow) =>
			Audio.ListDevices(flow, DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent)
				.Any(For(flow).IsHeadset);

		return Live(EDataFlow.Render)
			&& (!Known(EDataFlow.Capture) || Live(EDataFlow.Capture));
	}

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

	// Умолчания первого запуска: гарнитура первой, за ней то, что стоит устройством по
	// умолчанию сейчас — обычно встроенные динамики и микрофон, куда и надо возвращаться.
	private static Config Defaults(EDataFlow flow)
	{
		List<string> priority = [Config.HeadsetPattern];
		if (Fallback(flow) is { } device)
		{
			priority.Add(device);
		}

		// Моно-канал гарнитуры Bluetooth запрещён обоим направлениям: стоит Windows сделать
		// его устройством по умолчанию — и стерео пропадает, звук идёт через HFP, 16 кГц.
		return flow == EDataFlow.Capture
			? new(priority, ["Hands-Free", "NGENUITY"])
			: new(priority, ["NGENUITY", "HyperX Virtual Audio Device", "Hands-Free"]);
	}

	private static string? Fallback(EDataFlow flow)
	{
		// Устройство по умолчанию к первому запуску уже может быть перехвачено виртуальным
		// драйвером — тогда запоминать его бессмысленно, берём первое живое подходящее.
		var role = flow == EDataFlow.Capture ? ERole.Communications : ERole.Multimedia;

		return (Audio.GetDefault(flow, role) is { } current && !_neverFallback.Blocks(current)
			? current
			: Audio.ListDevices(flow, DeviceState.Active).FirstOrDefault(device => !_neverFallback.Blocks(device)))?.Name;
	}
}
