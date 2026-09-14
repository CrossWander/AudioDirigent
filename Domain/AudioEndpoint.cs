using System;

namespace AudioDirigent;

internal enum EDataFlow { Render, Capture, All }

[Flags]
internal enum DeviceState : uint
{
	Active = 0x1,
	Disabled = 0x2,
	NotPresent = 0x4,
	Unplugged = 0x8,
	All = 0xF,
}

/// <summary>Чем устройство является по мнению Windows. Значения — те, что отдаёт Core Audio.</summary>
internal enum FormFactor
{
	RemoteNetwork,
	Speakers,
	LineLevel,
	Headphones,
	Microphone,
	Headset,
	Handset,
	DigitalPassthrough,
	Spdif,
	Hdmi,
	Unknown,
}

/// <param name="Node">Путь PnP устройства за эндпоинтом; null — Windows его не отдала.</param>
/// <param name="Container">Корпус, общий для всех эндпоинтов одной железки; null — неизвестен.</param>
internal sealed record AudioEndpoint(
	string Id,
	string Name,
	DeviceState State,
	EDataFlow Flow,
	FormFactor Form,
	string Bus,
	string? Node,
	Guid? Container)
{
	/// <summary>Устройство подключено по Bluetooth — хоть музыкой, хоть телефонным профилем.</summary>
	public bool Bluetooth => Bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Телефонный профиль гарнитуры Bluetooth: моно, 16 кГц, голос. Windows заводит его
	/// отдельной шиной, поэтому признак не зависит ни от имени устройства, ни от языка системы.
	/// </summary>
	public bool HandsFree => Bus.Equals("BTHHFENUM", StringComparison.OrdinalIgnoreCase);

	/// <summary>Устройство существует только в виде драйвера: виртуальные кабели, микшеры, стримингс.</summary>
	public bool Software => Bus.Equals("SWD", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Устройство появилось потому, что его воткнули рукой, — USB, разъём, HDMI. В отличие
	/// от Bluetooth, который соединяется сам, это уже поступок, и он означает намерение.
	/// </summary>
	public bool Plugged => !Bluetooth && !Software;
}
