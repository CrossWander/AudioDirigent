using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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
	// DEVPROP_TYPE_BYTE. Не перепутать с 0x11: это DEVPROP_TYPE_BOOLEAN, и на нём проверка
	// не совпадала никогда — заряд молчал ровно так же, как если бы его не было.
	private const uint _typeByte = 0x0000_0003;

	private const int _crSuccess = 0;

	// Свойство висит не на звуковом узле, а на самом устройстве Bluetooth, и сколько между
	// ними промежуточных узлов — зависит от стека. Поднимаемся, пока не найдём или не упрёмся.
	private const int _depth = 4;

	// Сколько соседей просмотреть, прежде чем сдаться: у радиомодуля их десятки.
	private const int _fanOut = 64;

	// MAX_DEVICE_ID_LEN с запасом.
	private const uint _idLength = 256;

	/// <summary>
	/// Заряд в процентах; null — его не знает никто. Источника два: свойство PnP, которое
	/// заполняет Windows, и объявления в эфире, которые она игнорирует. Порядок именно
	/// такой: своё число система знает точно, эфирное приходит с задержкой.
	/// </summary>
	public static Charge? Of(string? node) =>
		FromWindows(node) is { } exact ? new Charge(exact, 1) : Beacon.Heard(Mac(node));

	/// <summary>Адрес Bluetooth устройства за эндпоинтом; null — оно не по радио.</summary>
	public static string? Mac(string? node)
	{
		// В пути музыкального профиля адрес стоит прямо, а телефонный приходит узлом вида
		// BTHHFENUM\BthHFPAudio\… — без адреса вовсе. Поэтому не только смотрим на путь, но
		// и поднимаемся по родителям: иначе одна и та же гарнитура показывала бы заряд в
		// одной своей строке и молчала в соседней.
		if (Find(node) is { } plain)
		{
			return plain;
		}

		if (string.IsNullOrEmpty(node) || CM_Locate_DevNode(out var devInst, node, 0) != _crSuccess)
		{
			return null;
		}

		for (var level = 0; level < _depth; level++)
		{
			if (Address(devInst) is { } address)
			{
				return address;
			}

			if (CM_Get_Parent(out var parent, devInst, 0) != _crSuccess)
			{
				return null;
			}

			devInst = parent;
		}

		return null;
	}

	/// <summary>Двенадцать шестнадцатеричных цифр подряд — так адрес стоит в имени узла.</summary>
	private static string? Find(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}

		// Берём последнее совпадение, а не первое: раньше адреса в имени службы стоит
		// хвост базового UUID Bluetooth, 00805F9B34FB, и он у всех устройств один.
		var matches = MacAddress().Matches(text);

		return matches.Count > 0 ? matches[^1].Value.ToUpperInvariant() : null;
	}

	private static int? FromWindows(string? node)
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

			// Заряд объявляет не та служба, через которую идёт звук: у гарнитуры Bluetooth
			// музыка висит на одной ветке, телефонный профиль на другой, а процент нашёлся
			// на второй — подъём по родителям мимо неё и проходит. Значит смотрим вбок: у
			// всех служб одного устройства в имени узла стоит один и тот же адрес.
			if (Address(devInst) is { } address && Siblings(devInst, address) is { } shared)
			{
				return shared;
			}

			if (CM_Get_Parent(out var parent, devInst, 0) != _crSuccess)
			{
				return null;
			}

			devInst = parent;
		}

		return null;
	}

	/// <summary>Заряд у соседней службы того же устройства; null — ни у одной его нет.</summary>
	private static int? Siblings(uint devInst, string address)
	{
		if (CM_Get_Parent(out var parent, devInst, 0) != _crSuccess
			|| CM_Get_Child(out var child, parent, 0) != _crSuccess)
		{
			return null;
		}

		// Соседей у радиомодуля столько, сколько спарено устройств помножить на их службы:
		// список конечный, но обходить его целиком незачем — свои службы лежат рядом.
		for (var seen = 0; seen < _fanOut; seen++)
		{
			if (child != devInst && Address(child) == address && Read(child) is { } percent)
			{
				return percent;
			}

			if (CM_Get_Sibling(out var next, child, 0) != _crSuccess)
			{
				return null;
			}

			child = next;
		}

		return null;
	}

	/// <summary>Адрес Bluetooth из имени узла — двенадцать шестнадцатеричных цифр подряд.</summary>
	private static string? Address(uint devInst)
	{
		// Буфер байтовый, а длина в знаках: LibraryImport отдаёт массив только того типа,
		// что лежит в памяти как есть, а char таковым не считается.
		var buffer = new byte[_idLength * 2];

		if (CM_Get_Device_ID(devInst, buffer, _idLength, 0) != _crSuccess)
		{
			return null;
		}

		return Find(Encoding.Unicode.GetString(buffer).TrimEnd('\0'));
	}

	[GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
	private static partial Regex MacAddress();

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

	[LibraryImport("cfgmgr32.dll")]
	private static partial int CM_Get_Child(out uint child, uint devInst, uint flags);

	[LibraryImport("cfgmgr32.dll")]
	private static partial int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

	[LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
	private static partial int CM_Get_Device_ID(uint devInst, [Out] byte[] buffer, uint length, uint flags);

	[LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
	private static partial int CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY key, out uint propertyType,
		[Out] byte[] buffer, ref uint bufferSize, uint flags);
}
