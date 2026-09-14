using System;
using System.Globalization;
using System.Linq;

namespace AudioDirigent;

/// <summary>
/// Устройство Bluetooth так, как его помнит радиомодуль. Адрес — единственное, что у него
/// не меняется: имя устройство сообщает само и может сменить, а список скрытых должен
/// пережить и это.
/// </summary>
/// <param name="Class">Класс устройства: им оно само себя называет — гарнитура, телефон, мышь.</param>
internal sealed record BluetoothDevice(ulong Address, string Name, uint Class, bool Connected, bool Paired)
{
	/// <summary>Адрес двенадцатью знаками — в таком виде он стоит в пути устройства PnP.</summary>
	public string Mac => Address.ToString("X12", CultureInfo.InvariantCulture);

	/// <summary>Адрес по-человечески, парами через двоеточие.</summary>
	public string Pretty => string.Join(':', Convert.FromHexString(Mac).Select(part => part.ToString("X2")));

	/// <summary>Старший класс 0x04 — всё, что звучит: гарнитуры, колонки, автомагнитолы.</summary>
	public bool Audio => ((Class >> 8) & 0x1F) == 0x04;

	/// <summary>Ключ подписи под именем: что это за устройство по его же словам.</summary>
	public string Kind => Audio
		? ((Class >> 2) & 0x3F) switch
		{
			1 or 2 => "langBluetoothHeadset",
			4 => "langBluetoothMicrophone",
			5 or 7 => "langBluetoothSpeaker",
			6 => "langBluetoothHeadphones",
			8 => "langBluetoothCar",
			_ => "langBluetoothAudio",
		}
		: "langBluetoothOther";
}
