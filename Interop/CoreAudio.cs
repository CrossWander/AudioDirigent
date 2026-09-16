using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AudioDirigent;

// Тонкая обёртка над Core Audio API (MMDevice) + недокументированный IPolicyConfig.
// Штатного публичного API для смены устройства по умолчанию в Windows нет —
// все существующие решения (SoundSwitch, AudioSwitcher, DefaultAudioChanger) идут этим же путём.

internal enum ERole { Console, Multimedia, Communications }

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

	// PKEY_Device_FriendlyName — «Наушники (Cloud III)»
	public static PropertyKey FriendlyName => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

	// PKEY_AudioEndpoint_FormFactor — чем устройство является: динамики, наушники, микрофон, HDMI.
	public static PropertyKey FormFactor => new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

	// PKEY_Device_EnumeratorName — по какой шине устройство пришло: USB, BTHENUM, HDAUDIO, SWD.
	// Windows сама ранжирует устройства по умолчанию с оглядкой на это же свойство.
	public static PropertyKey Bus => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

	// Путь PnP того устройства, за которым стоит эндпоинт: «{1}.USB\VID_03F0&PID_089D&MI_00\…».
	// Недокументирован, но живёт с Vista и лежит в реестре рядом с остальными свойствами
	// эндпоинта. Документированный PKEY_Device_InstanceId не подходит: он описывает сам
	// эндпоинт (SWD\MMDEVAPI\…), а на Windows 10 попросту пуст.
	public static PropertyKey Node => new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

	// System.Devices.ContainerId — общий для всех эндпоинтов одной железки. Наушники,
	// микрофон и телефонный профиль гарнитуры приходят разными узлами, а корпус у них один.
	public static PropertyKey Container => new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
}

// Урезанный PROPVARIANT: нужны только строки и числа. Буфер намеренно больше настоящих
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

	private const ushort VT_UI4 = 19;
	private const ushort VT_LPWSTR = 31;
	private const ushort VT_CLSID = 72;

	public string? AsString() => VarType == VT_LPWSTR ? Marshal.PtrToStringUni(Pointer) : null;

	/// <summary>Число лежит в первых четырёх байтах того же поля, что и указатель.</summary>
	public uint? AsNumber() => VarType == VT_UI4 ? (uint)(Pointer.ToInt64() & 0xFFFFFFFF) : null;

	/// <summary>Идентификатор лежит не в самой структуре, а по указателю из неё.</summary>
	public Guid? AsGuid() =>
		VarType == VT_CLSID && Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(Pointer) : null;
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
	void NotImpl_SetChannelVolumeLevel();
	void NotImpl_SetChannelVolumeLevelScalar();
	void NotImpl_GetChannelVolumeLevel();
	void NotImpl_GetChannelVolumeLevelScalar();
	void NotImpl_SetMute();
	void NotImpl_GetMute();
	void NotImpl_GetVolumeStepInfo();
	void NotImpl_VolumeStepUp();
	void NotImpl_VolumeStepDown();
	void NotImpl_QueryHardwareSupport();

	// Диапазон нужен, чтобы отличить узел самой точки от железных: у них он совпадает.
	void GetVolumeRange(out float minimum, out float maximum, out float increment);
}

/// <summary>Пиковый уровень сигнала — им рисуется полоска под ползунком чувствительности.</summary>
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
	float GetPeakValue();
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

internal static class Audio
{
	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant pvar);

	private static IMMDeviceEnumerator CreateEnumerator() =>
		(IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

	/// <summary>Устройство со всем, что о нём знает Windows; null — у него нет даже имени.</summary>
	private static AudioEndpoint? Describe(IMMDevice device, EDataFlow flow)
	{
		IPropertyStore store;
		try
		{
			store = device.OpenPropertyStore(0 /* STGM_READ */);
		}
		catch (COMException)
		{
			return null;
		}

		// У части эндпоинтов (обычно отключённых) имени нет вовсе — GetValue отдаёт ERROR_NOT_FOUND.
		if (Text(store, PropertyKey.FriendlyName) is not { } name)
		{
			return null;
		}

		var form = Number(store, PropertyKey.FormFactor) is { } value && value <= (uint)FormFactor.Unknown
			? (FormFactor)value
			: FormFactor.Unknown;

		// Путь приходит с префиксом «{1}.», который к самому устройству отношения не имеет.
		var node = Text(store, PropertyKey.Node) is { } path && path.IndexOf('.') is var dot and >= 0
			? path[(dot + 1)..]
			: null;

		return new AudioEndpoint(
			device.GetId(),
			name,
			device.GetState(),
			flow,
			form,
			Text(store, PropertyKey.Bus) ?? string.Empty,
			node,
			Read(store, PropertyKey.Container, value => value.AsGuid()));
	}

	private static string? Text(IPropertyStore store, PropertyKey key) => Read(store, key, value => value.AsString());

	private static uint? Number(IPropertyStore store, PropertyKey key) => Read(store, key, value => value.AsNumber());

	private static T? Read<T>(IPropertyStore store, PropertyKey key, Func<PropVariant, T?> convert)
	{
		PropVariant value = default;
		try
		{
			value = store.GetValue(ref key);
			return convert(value);
		}
		catch (COMException)
		{
			return default;
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
			if (Describe(device, flow) is { } endpoint)
			{
				result.Add(endpoint);
			}
		}

		return result;
	}

	public static AudioEndpoint? GetDefault(EDataFlow flow, ERole role)
	{
		try
		{
			var device = CreateEnumerator().GetDefaultAudioEndpoint(flow, role);

			// Имя есть всегда: устройством по умолчанию безымянный эндпоинт не бывает.
			// Но если Windows его не отдала, устройство от этого не перестало играть, и
			// показать тогда нечего, кроме самого эндпоинта, — зато сразу видно, кто это.
			var id = device.GetId();

			return Describe(device, flow)
				?? new AudioEndpoint(
					id,
					id,
					device.GetState(),
					flow,
					FormFactor.Unknown,
					string.Empty,
					null,
					null);
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

	/// <summary>Диапазон громкости точки в децибелах; null — точка исчезла.</summary>
	public static (float Minimum, float Maximum)? VolumeRange(string deviceId)
	{
		try
		{
			if (Endpoint(deviceId) is not { } volume)
			{
				return null;
			}

			volume.GetVolumeRange(out var minimum, out var maximum, out _);

			return (minimum, maximum);
		}
		catch (COMException)
		{
			return null;
		}
	}

	/// <summary>
	/// Достать у устройства одну из его служб. Null значит «не отозвалось» и ничего больше:
	/// устройство могло исчезнуть между выбором и настройкой, и это обычное дело.
	/// </summary>
	internal const uint ClsCtxAll = 23;

	/// <inheritdoc cref="Activate{T}(string, uint)"/>
	internal static T? Activate<T>(string deviceId, uint clsCtx = 0 /* CLSCTX_INPROC_SERVER */)
		where T : class
	{
		var iid = typeof(T).GUID;
		try
		{
			var device = CreateEnumerator().GetDevice(deviceId);

			return device.Activate(ref iid, clsCtx, IntPtr.Zero, out var raw) == 0 ? raw as T : null;
		}
		catch (COMException)
		{
			return null;
		}
	}

	private static IAudioEndpointVolume? Endpoint(string deviceId) =>
		Activate<IAudioEndpointVolume>(deviceId);

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
