using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AudioDirigent;

// Тонкая обёртка над Core Audio API (MMDevice) + недокументированный IPolicyConfig.
// Штатного публичного API для смены устройства по умолчанию в Windows нет —
// все существующие решения (SoundSwitch, AudioSwitcher, DefaultAudioChanger) идут этим же путём.

internal enum EDataFlow { Render, Capture, All }

internal enum ERole { Console, Multimedia, Communications }

[Flags]
internal enum DeviceState : uint
{
	Active = 0x1,
	Disabled = 0x2,
	NotPresent = 0x4,
	Unplugged = 0x8,
	All = 0xF,
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
	IMMDeviceCollection EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask);
	IMMDevice GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role);
	IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id);
	void RegisterEndpointNotificationCallback(IMMNotificationClient client);
	void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
	uint GetCount();
	IMMDevice Item(uint index);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
	[PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
	IPropertyStore OpenPropertyStore(uint stgmAccess);
	[return: MarshalAs(UnmanagedType.LPWStr)] string GetId();
	DeviceState GetState();
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
	uint GetCount();
	PropertyKey GetAt(uint index);
	PropVariant GetValue(ref PropertyKey key);
	void SetValue(ref PropertyKey key, ref PropVariant value);
	void Commit();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
	public Guid FormatId;
	public int PropertyId;

	public PropertyKey(Guid formatId, int propertyId)
	{
		FormatId = formatId;
		PropertyId = propertyId;
	}

	// PKEY_Device_FriendlyName — «Наушники (HyperX Cloud III)»
	public static PropertyKey FriendlyName => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

// Урезанный PROPVARIANT: нам нужен только VT_LPWSTR. Буфер намеренно больше настоящих
// 24 байт (x64), чтобы COM гарантированно писал внутрь нашего стека.
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
	public ushort VarType;
	private readonly ushort _reserved1;
	private readonly ushort _reserved2;
	private readonly ushort _reserved3;
	public IntPtr Pointer;
	private readonly IntPtr _tail1;
	private readonly IntPtr _tail2;

	private const ushort VT_LPWSTR = 31;

	public string? AsString() => VarType == VT_LPWSTR ? Marshal.PtrToStringUni(Pointer) : null;
}

// Недокументированный, но стабильный с Windows 7: единственный способ сменить устройство по умолчанию.
// Порядок методов — это порядок в таблице вызовов, поэтому лишние объявлены заглушками.
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
	void RegisterControlChangeNotify(IntPtr notify);
	void UnregisterControlChangeNotify(IntPtr notify);
	uint GetChannelCount();
	void SetMasterVolumeLevel(float decibels, IntPtr context);
	void SetMasterVolumeLevelScalar(float level, IntPtr context);
	float GetMasterVolumeLevel();
	float GetMasterVolumeLevelScalar();
}

[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
	void NotImpl_GetMixFormat();
	void NotImpl_GetDeviceFormat();
	void NotImpl_ResetDeviceFormat();
	void NotImpl_SetDeviceFormat();
	void NotImpl_GetProcessingPeriod();
	void NotImpl_SetProcessingPeriod();
	void NotImpl_GetShareMode();
	void NotImpl_SetShareMode();
	void NotImpl_GetPropertyValue();
	void NotImpl_SetPropertyValue();
	void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
	void NotImpl_SetEndpointVisibility();
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClientComObject { }

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
	void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);
	void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
	void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
	void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
	void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

internal sealed record AudioEndpoint(string Id, string Name, DeviceState State);

internal static class Audio
{
	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant pvar);

	private static IMMDeviceEnumerator CreateEnumerator() =>
		(IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

	/// <summary>Имя устройства; null — Windows его не отдаёт.</summary>
	private static string? Describe(IMMDevice device)
	{
		// У части эндпоинтов (обычно отключённых) имени нет вовсе — GetValue отдаёт ERROR_NOT_FOUND.
		PropVariant value = default;
		try
		{
			var store = device.OpenPropertyStore(0 /* STGM_READ */);
			var key = PropertyKey.FriendlyName;
			value = store.GetValue(ref key);
			return value.AsString();
		}
		catch (COMException)
		{
			return null;
		}
		finally
		{
			PropVariantClear(ref value);
		}
	}

	public static List<AudioEndpoint> ListDevices(EDataFlow flow, DeviceState mask = DeviceState.All)
	{
		var collection = CreateEnumerator().EnumAudioEndpoints(flow, mask);
		var count = collection.GetCount();
		var result = new List<AudioEndpoint>((int)count);
		for (uint i = 0; i < count; i++)
		{
			var device = collection.Item(i);

			// Безымянные эндпоинты — следы удалённых устройств: правило по имени их
			// всё равно не поймает, а список они забивают.
			if (Describe(device) is { } name)
			{
				result.Add(new AudioEndpoint(device.GetId(), name, device.GetState()));
			}
		}

		return result;
	}

	public static AudioEndpoint? GetDefault(EDataFlow flow, ERole role)
	{
		try
		{
			var device = CreateEnumerator().GetDefaultAudioEndpoint(flow, role);
			return new AudioEndpoint(device.GetId(), Describe(device) ?? Localization.Get("langNoName"), device.GetState());
		}
		catch (COMException)
		{
			return null; // устройств этого направления нет вообще
		}
	}

	/// <summary>Поставить громкость устройства в процентах; false — устройство её не отдаёт.</summary>
	public static bool SetVolume(string deviceId, int percent)
	{
		if (Endpoint(deviceId) is not { } volume)
		{
			return false;
		}

		try
		{
			volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, IntPtr.Zero);

			return true;
		}
		catch (COMException)
		{
			return false;
		}
	}

	/// <summary>Громкость устройства в процентах; null — устройство её не отдаёт.</summary>
	public static int? GetVolume(string deviceId)
	{
		try
		{
			return Endpoint(deviceId) is { } volume
				? (int)Math.Round(volume.GetMasterVolumeLevelScalar() * 100)
				: null;
		}
		catch (COMException)
		{
			return null;
		}
	}

	private static IAudioEndpointVolume? Endpoint(string deviceId)
	{
		var iid = typeof(IAudioEndpointVolume).GUID;
		try
		{
			var device = CreateEnumerator().GetDevice(deviceId);

			return device.Activate(ref iid, 0 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var raw) == 0
				? raw as IAudioEndpointVolume
				: null;
		}
		catch (COMException)
		{
			// устройство исчезло между выбором и настройкой громкости
			return null;
		}
	}

	public static void SetDefault(string deviceId)
	{
		var policy = (IPolicyConfig)new PolicyConfigClientComObject();
		foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
		{
			policy.SetDefaultEndpoint(deviceId, role);
		}
	}

	public static IDisposable Subscribe(IMMNotificationClient client)
	{
		var enumerator = CreateEnumerator();
		enumerator.RegisterEndpointNotificationCallback(client);
		return new Subscription(enumerator, client);
	}

	private sealed class Subscription(IMMDeviceEnumerator enumerator, IMMNotificationClient client) : IDisposable
	{
		public void Dispose() => enumerator.UnregisterEndpointNotificationCallback(client);
	}
}
