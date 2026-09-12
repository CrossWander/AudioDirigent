using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Подключение и отключение гарнитуры Bluetooth — то же, что делает кнопка «Подключить»
/// в параметрах Windows. Путь неочевидный: от конечной точки звука вниз по топологии к
/// KS-фильтру устройства и команда ему.
///
/// Через Bluetooth API это не делается: BluetoothSetServiceState ставит и снимает драйвер
/// профиля, а не поднимает связь, и «подключением» там работает пара снять-поставить —
/// секунды ожидания и мигание устройства в списке.
/// </summary>
internal static class BluetoothAudio
{
	private static readonly Guid _btAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
	private const uint _reconnect = 0;
	private const uint _disconnect = 1;
	private const uint _propertyGet = 1;
	private const uint _clsCtxAll = 23;

	/// <summary>Поднять связь с устройством; false — команду не приняли.</summary>
	public static bool Connect(AudioEndpoint device) => Command(device, _reconnect);

	/// <summary>Разорвать связь; false — команду не приняли.</summary>
	public static bool Disconnect(AudioEndpoint device) => Command(device, _disconnect);

	/// <summary>
	/// Команда уходит всем конечным точкам одной железки. Наушники, микрофон и телефонный
	/// профиль — три разные точки одного корпуса, и поднимать надо все: иначе музыка придёт,
	/// а микрофон гарнитуры останется отключённым. Устройство отцепляется тем же правилом —
	/// пока жива хоть одна точка, связь держится.
	/// </summary>
	private static bool Command(AudioEndpoint device, uint property)
	{
		var done = false;
		foreach (var endpoint in Family(device))
		{
			done |= Send(endpoint.Id, property);
		}

		return done;
	}

	/// <summary>Все конечные точки того же корпуса, включая саму эту.</summary>
	private static List<AudioEndpoint> Family(AudioEndpoint device)
	{
		if (device.Container is not { } container)
		{
			return [device];
		}

		return
		[
			.. Audio.ListDevices(EDataFlow.Render)
				.Concat(Audio.ListDevices(EDataFlow.Capture))
				.Where(endpoint => endpoint.Container == container)
		];
	}

	private static bool Send(string endpointId, uint property)
	{
		try
		{
			// Конечная точка — только верхушка: команду принимает KS-фильтр устройства,
			// а до него надо спуститься по топологии через разъём, которым точка подключена.
			if (Activate<IDeviceTopology>(endpointId) is not { } topology)
			{
				return false;
			}

			topology.GetConnector(0, out var connector);
			connector.GetDeviceIdConnectedTo(out var filterId);

			// Спуск заканчивается устройством Bluetooth; всё прочее эту команду не поймёт.
			if (filterId?.Contains("bth", StringComparison.OrdinalIgnoreCase) != true
				|| Activate<IKsControl>(filterId) is not { } control)
			{
				return false;
			}

			var request = new KsIdentifier { Set = _btAudio, Id = property, Flags = _propertyGet };

			// Ответ приходит сразу, а связь поднимается ещё секунду-другую: следить за ней
			// надо по событиям Core Audio, а не по возвращённому значению.
			return control.KsProperty(ref request, (uint)Marshal.SizeOf<KsIdentifier>(), IntPtr.Zero, 0, out _) == 0;
		}
		catch (COMException)
		{
			// Устройство исчезло, пока мы шли к нему по топологии.
			return false;
		}
	}

	private static T? Activate<T>(string deviceId) where T : class
	{
		var iid = typeof(T).GUID;
		var device = ((IMMDeviceEnumerator)new MMDeviceEnumeratorComObject()).GetDevice(deviceId);

		return device.Activate(ref iid, _clsCtxAll, IntPtr.Zero, out var raw) == 0 ? raw as T : null;
	}

	// KSIDENTIFIER: набор, номер свойства и вид обращения.
	[StructLayout(LayoutKind.Sequential)]
	private struct KsIdentifier
	{
		public Guid Set;
		public uint Id;
		public uint Flags;
	}

	[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDeviceTopology
	{
		uint GetConnectorCount();
		void GetConnector(uint index, out IConnector connector);
		// Дальше идут подузлы и поиск по номеру — нам нужен только первый разъём.
	}

	[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IConnector
	{
		void NotImpl_GetType();
		void NotImpl_GetDataFlow();
		void NotImpl_ConnectTo();
		void NotImpl_Disconnect();
		void NotImpl_IsConnected();
		void NotImpl_GetConnectedTo();
		void NotImpl_GetConnectorIdConnectedTo();
		void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
	}

	[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IKsControl
	{
		[PreserveSig]
		int KsProperty(ref KsIdentifier property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
	}
}
