using System;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Заряд устройства, если Windows его знает. Живёт в недокументированном свойстве PnP —
/// том самом, которое показывают «Параметры» рядом со спаренной гарнитурой. Публичного
/// API для этого нет: WinRT читает ровно этот же ключ из той же базы, только дороже.
///
/// Отдаёт его меньшинство устройств. Гарнитура на приёмнике 2,4 ГГц — никогда: для системы
/// это обычное устройство HID, и заряд знает лишь протокол производителя. Для Bluetooth —
/// только если стек его подхватил. Поэтому «нет заряда» здесь нормальное состояние, а не сбой.
/// </summary>
internal static partial class Battery
{
	// DEVPKEY_Bluetooth_Battery: один байт, проценты.
	private static readonly Guid _format = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
	private const int _propertyId = 2;
	private const uint _typeByte = 0x0000_0011;

	private const int _crSuccess = 0;

	// Свойство висит не на звуковом узле, а на самом устройстве Bluetooth, и сколько между
	// ними промежуточных узлов — зависит от стека. Поднимаемся, пока не найдём или не упрёмся.
	private const int _depth = 4;

	/// <summary>Заряд в процентах для узла PnP; null — Windows его не знает.</summary>
	public static int? Percent(string? node)
	{
		if (string.IsNullOrEmpty(node) || CM_Locate_DevNode(out var devInst, node, 0) != _crSuccess)
		{
			return null;
		}

		for (var level = 0; level < _depth; level++)
		{
			if (Read(devInst) is { } percent)
			{
				return percent;
			}

			if (CM_Get_Parent(out var parent, devInst, 0) != _crSuccess)
			{
				return null;
			}

			devInst = parent;
		}

		return null;
	}

	private static int? Read(uint devInst)
	{
		var key = new DEVPROPKEY { FormatId = _format, PropertyId = _propertyId };
		var value = new byte[1];
		var size = (uint)value.Length;

		var result = CM_Get_DevNode_Property(devInst, ref key, out var type, value, ref size, 0);

		// Байт свойства не бывает больше одного, но чужое свойство с тем же ключом лучше
		// не разбирать вовсе: проценты — это ровно DEVPROP_TYPE_BYTE.
		return result == _crSuccess && type == _typeByte && size == 1 ? value[0] : null;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DEVPROPKEY
	{
		public Guid FormatId;
		public int PropertyId;
	}

	[LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

	[LibraryImport("cfgmgr32.dll")]
	private static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

	[LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
	private static partial int CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY key, out uint propertyType,
		[Out] byte[] buffer, ref uint bufferSize, uint flags);
}
