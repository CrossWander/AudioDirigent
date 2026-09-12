using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioDirigent;

/// <summary>
/// Опрос донгла HyperX по HID. Нужен потому, что донгл виден системе как активное
/// аудиоустройство даже когда гарнитура выключена — состояние Core Audio об этом молчит.
/// Протокол разобран в проекте platorp/HyperX-Cloud-III-3-S-Audio-Switcher.
/// </summary>
internal sealed partial class HeadsetProbe : IDisposable
{
	// HyperX выпускается под двумя VID: 0x03F0 — HP, 0x0951 — Kingston (модели до 2021).
	private static readonly ushort[] _hyperXVendorIds = [0x03F0, 0x0951];

	private const ushort _controlUsagePage = 0x1C0;
	private const byte _statusReportId = 0x0C;
	private static readonly byte[] _statusRequest = [_statusReportId, 0x02, 0x03, 0x01, 0x00, 0x02];

	// В ответе шестой байт: 2 — гарнитура включена и в эфире, 0 — выключена.
	private const int _statusByteIndex = 6;
	private const byte _statusOn = 2;

	private readonly FileStream _stream;
	private readonly int _inputLength;
	private readonly int _outputLength;

	public ushort ProductId { get; }

	private HeadsetProbe(FileStream stream, ushort productId, int inputLength, int outputLength)
	{
		_stream = stream;
		_inputLength = inputLength;
		_outputLength = outputLength;
		ProductId = productId;
	}

	/// <summary>Устройство HyperX: HP либо Kingston.</summary>
	public static bool IsHyperX(ushort vendorId) => _hyperXVendorIds.Contains(vendorId);

	/// <summary>Находит управляющий HID-интерфейс донгла HyperX; null — донгла нет.</summary>
	public static HeadsetProbe? Open()
	{
		// У проводных моделей коллекция с этой usage page тоже есть, но с нулевыми длинами
		// отчётов: слать и читать нечего. Без этой проверки опрос ушёл бы в пустоту.
		var candidates = Enumerate().Where(d =>
			IsHyperX(d.VendorId)
			&& d.UsagePage == _controlUsagePage
			&& d.InputLength > _statusByteIndex
			&& d.OutputLength >= _statusRequest.Length);

		foreach (var device in candidates)
		{
			if (OpenStream(device.Path) is { } stream)
			{
				return new HeadsetProbe(stream, device.ProductId, device.InputLength, device.OutputLength);
			}
		}

		return null;
	}

	/// <summary>true — гарнитура включена, false — выключена, null — донгл не ответил.</summary>
	public bool? IsHeadsetOn()
	{
		var response = Query();
		return response is not null && response.Length > _statusByteIndex
			? response[_statusByteIndex] == _statusOn
			: null;
	}

	/// <summary>Сырой ответ донгла на запрос статуса; null — не ответил. Нужен режиму --hid.</summary>
	public byte[]? Query()
	{
		try
		{
			var request = new byte[Math.Max(_outputLength, _statusRequest.Length)];
			_statusRequest.CopyTo(request, 0);
			_stream.Write(request, 0, request.Length);
			_stream.Flush();

			var response = new byte[Math.Max(_inputLength, _statusByteIndex + 1)];
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

	internal sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage, int InputLength, int OutputLength);

	/// <summary>Все HID-интерфейсы в системе — используется режимом --hid для разбора протокола.</summary>
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
