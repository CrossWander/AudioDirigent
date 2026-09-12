using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioDirigent;

/// <summary>
/// Опрос приёмника беспроводной гарнитуры по HID. Нужен потому, что приёмник виден системе
/// как активное аудиоустройство даже когда гарнитура выключена, а средствами Windows это
/// не выясняется никак: драйвер строит описание разъёма из дескрипторов, и состояние
/// радиоканала туда не попадает. Протокол у каждого производителя свой, поэтому программа
/// его не знает — байты описаны в config.json. Пример рабочего протокола лежит в README.
/// </summary>
internal sealed partial class DeviceProbe : IDisposable
{
	private readonly FileStream _stream;
	private readonly ProbeRule _rule;
	private readonly byte[] _request;
	private readonly byte _on;
	private readonly int _inputLength;
	private readonly int _outputLength;

	public ushort ProductId { get; }

	private DeviceProbe(FileStream stream, ProbeRule rule, byte[] request, byte on, HidDeviceInfo device)
	{
		_stream = stream;
		_rule = rule;
		_request = request;
		_on = on;
		_inputLength = device.InputLength;
		_outputLength = device.OutputLength;
		ProductId = device.ProductId;
	}

	/// <summary>Открыть приёмник, описанный в config.json; null — он не описан или не найден.</summary>
	public static DeviceProbe? Open()
	{
		if (Store.Current.Probe is not { } rule
			|| Bytes(rule.Request) is not { Length: > 0 } request
			|| Byte(rule.UsagePage, out var usagePageHigh) is not { } usagePage
			|| Byte(rule.OnValue) is not { } on)
		{
			return null;
		}

		var vendors = rule.Vendors.Select(value => Word(value)).OfType<ushort>().ToList();
		var page = (ushort)((usagePageHigh << 8) | usagePage);

		// У проводных моделей коллекция с той же usage page тоже бывает, но с нулевыми
		// длинами отчётов: слать и читать нечего. Без проверки опрос ушёл бы в пустоту.
		var candidates = Enumerate().Where(device =>
			(vendors.Count == 0 || vendors.Contains(device.VendorId))
			&& device.UsagePage == page
			&& device.InputLength > rule.StatusByte
			&& device.OutputLength >= request.Length);

		foreach (var device in candidates)
		{
			if (OpenStream(device.Path) is { } stream)
			{
				return new DeviceProbe(stream, rule, request, on, device);
			}
		}

		return null;
	}

	/// <summary>true — гарнитура включена, false — выключена, null — приёмник не ответил.</summary>
	public bool? IsOn()
	{
		var response = Query();

		return response is not null && response.Length > _rule.StatusByte
			? response[_rule.StatusByte] == _on
			: null;
	}

	/// <summary>Устройство принадлежит гарнитуре, за которой следит приёмник.</summary>
	public bool Covers(AudioEndpoint device) =>
		_rule.Covers.Length > 0 && Config.Matches(device, _rule.Covers);

	/// <summary>Сырой ответ приёмника на запрос состояния; null — не ответил. Нужен режиму --devices.</summary>
	public byte[]? Query()
	{
		try
		{
			var request = new byte[Math.Max(_outputLength, _request.Length)];
			_request.CopyTo(request, 0);
			_stream.Write(request, 0, request.Length);
			_stream.Flush();

			var response = new byte[Math.Max(_inputLength, _rule.StatusByte + 1)];
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			var read = _stream.ReadAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult();

			return read > 0 ? response[..read] : null;
		}
		catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
		{
			return null;
		}
	}

	public void Dispose() => _stream.Dispose();

	/// <summary>Последовательность байт «0C 02 03»; null — в строке не только шестнадцатеричные числа.</summary>
	private static byte[]? Bytes(string value)
	{
		var parts = value.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
		var result = new byte[parts.Length];
		for (var i = 0; i < parts.Length; i++)
		{
			if (Byte(parts[i]) is not { } parsed)
			{
				return null;
			}

			result[i] = parsed;
		}

		return result;
	}

	private static byte? Byte(string value) =>
		byte.TryParse(Trim(value), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

	/// <summary>Младший байт значения; старший уходит в <paramref name="high"/>. Для usage page вида 01C0.</summary>
	private static byte? Byte(string value, out byte high)
	{
		high = 0;
		if (Word(value) is not { } parsed)
		{
			return null;
		}

		high = (byte)(parsed >> 8);

		return (byte)(parsed & 0xFF);
	}

	private static ushort? Word(string value) =>
		ushort.TryParse(Trim(value), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

	// Число пишут и как 0x1C0, и как 1C0 — обе записи об одном и том же.
	private static string Trim(string value) =>
		value.Trim().TrimStart('0', 'x', 'X') is { Length: > 0 } trimmed ? trimmed : "0";

	internal sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage, int InputLength, int OutputLength);

	/// <summary>Все HID-интерфейсы в системе — используется режимом --devices для разбора протокола.</summary>
	internal static IEnumerable<HidDeviceInfo> Enumerate()
	{
		HidD_GetHidGuid(out var hidGuid);
		var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, _digcfPresent | _digcfDeviceInterface);
		if (set == _invalidHandle)
		{
			yield break;
		}

		try
		{
			var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
			for (var index = 0u; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData); index++)
			{
				if (ReadDevicePath(set, ref interfaceData) is { } path && Describe(path) is { } info)
				{
					yield return info;
				}
			}
		}
		finally
		{
			SetupDiDestroyDeviceInfoList(set);
		}
	}

	private static string? ReadDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA interfaceData)
	{
		SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
		if (required == 0)
		{
			return null;
		}

		var buffer = Marshal.AllocHGlobal(required);
		try
		{
			// cbSize описывает саму структуру, а не буфер: 8 на x64, 6 на x86.
			Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
			return SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, out _, IntPtr.Zero)
				? Marshal.PtrToStringUni(buffer + 4)
				: null;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static HidDeviceInfo? Describe(string path)
	{
		// Только метаданные: без доступа на чтение/запись, иначе занятое устройство не опросить.
		using var handle = CreateFile(path, 0, _fileShareRead | _fileShareWrite, IntPtr.Zero, _openExisting, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return null;
		}

		var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
		if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out var preparsed))
		{
			return null;
		}

		try
		{
			return HidP_GetCaps(preparsed, out var caps) == _hidpStatusSuccess
				? new HidDeviceInfo(path, attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
					caps.InputReportByteLength, caps.OutputReportByteLength)
				: null;
		}
		finally
		{
			HidD_FreePreparsedData(preparsed);
		}
	}

	private static FileStream? OpenStream(string path)
	{
		var handle = CreateFile(path, _genericRead | _genericWrite, _fileShareRead | _fileShareWrite,
			IntPtr.Zero, _openExisting, _fileFlagOverlapped, IntPtr.Zero);

		if (handle.IsInvalid)
		{
			handle.Dispose();
			return null;
		}

		return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
	}

	private const int _digcfPresent = 0x02;
	private const int _digcfDeviceInterface = 0x10;
	private const uint _genericRead = 0x80000000;
	private const uint _genericWrite = 0x40000000;
	private const uint _fileShareRead = 0x01;
	private const uint _fileShareWrite = 0x02;
	private const uint _openExisting = 3;
	private const uint _fileFlagOverlapped = 0x40000000;
	private const int _hidpStatusSuccess = 0x00110000;
	private static readonly IntPtr _invalidHandle = new(-1);

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public int cbSize;
		public Guid InterfaceClassGuid;
		public int Flags;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct HIDD_ATTRIBUTES
	{
		public int Size;
		public ushort VendorID;
		public ushort ProductID;
		public ushort VersionNumber;
	}

	// Реальная HIDP_CAPS — 32 поля ushort; нужны только первые пять, остальное держим размером.
	[StructLayout(LayoutKind.Sequential, Size = 64)]
	private struct HIDP_CAPS
	{
		public ushort Usage;
		public ushort UsagePage;
		public ushort InputReportByteLength;
		public ushort OutputReportByteLength;
		public ushort FeatureReportByteLength;
	}

	[LibraryImport("hid.dll")]
	private static partial void HidD_GetHidGuid(out Guid hidGuid);

	[LibraryImport("hid.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

	[LibraryImport("hid.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

	[LibraryImport("hid.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

	[LibraryImport("hid.dll")]
	private static partial int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

	[LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

	[LibraryImport("setupapi.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
		ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

	[LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
		ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, int detailSize, out int required, IntPtr deviceInfoData);

	[LibraryImport("setupapi.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share,
		IntPtr security, uint creationDisposition, uint flags, IntPtr template);
}
