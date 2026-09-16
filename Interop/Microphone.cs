using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Ручка на пути сигнала микрофона. Уровень конечной точки — не единственное, чем можно
/// его править: усиление и автоподстройка сидят узлами в топологии устройства, и до них
/// надо спускаться от точки вниз, к железу.
/// </summary>
/// <param name="Name">Как узел называет себя сам — им же подписан ползунок в параметрах Windows.</param>
/// <param name="Control">Сам узел. Без него каждый шаг ползунка заново обходил бы всю топологию.</param>
internal sealed record MicrophoneKnob(string Name, MicrophoneKnobKind Kind, uint Part,
	float Minimum, float Maximum, float Step, float Value, bool On, object Control);

internal enum MicrophoneKnobKind
{
	/// <summary>Громкость в децибелах. Их бывает несколько: своя у точки, своя у железа.</summary>
	Level,

	/// <summary>Автоподстройка усиления — тот самый «то тихо, то орёт».</summary>
	AutoGain,
}

/// <summary>
/// Подстройка микрофона средствами самого устройства. Всё, что отсюда ставится, Windows
/// запоминает за конечной точкой и восстанавливает сама — программа нужна только в момент
/// правки, дальше настройка живёт без неё.
/// </summary>
internal static class Microphone
{
	private const uint _clsCtxAll = 23;

	// Узлы топологии зовут себя подтипом, а не именем: имя у них бывает пустым.
	private static readonly Guid _volumeNode = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");
	private static readonly Guid _autoGainNode = new("E88C9BA0-C557-11D0-8A2B-00A0C9255AC1");

	/// <summary>
	/// Всё, что устройство даёт крутить. Пустой список — драйвер не отдал ничего, и кроме
	/// уровня конечной точки у этого микрофона ничего нет.
	/// </summary>
	/// <param name="endpointId">Конечная точка, от разъёма которой идти вниз, к железу.</param>
	/// <param name="trace">Куда писать ход обхода; нужен разбору из командной строки.</param>
	public static List<MicrophoneKnob> Knobs(string endpointId, Action<string>? trace = null)
	{
		var found = new List<MicrophoneKnob>();
		Each(endpointId, (part, depth) => Inspect(part, found, depth, trace), trace);

		return Without(found, Audio.VolumeRange(endpointId));
	}

	/// <summary>Поставить железному узлу уровень в децибелах; false — узел не отозвался.</summary>
	public static bool SetLevel(MicrophoneKnob knob, float decibels) =>
		knob.Control is IAudioVolumeLevel level && level.SetLevelUniform(decibels, IntPtr.Zero) == 0;

	/// <summary>Включить или выключить автоподстройку; false — узел не отозвался.</summary>
	public static bool SetAutoGain(MicrophoneKnob knob, bool on) =>
		knob.Control is IAudioAutoGainControl gain && gain.SetEnabled(on ? 1 : 0, IntPtr.Zero) == 0;

	/// <summary>
	/// Уровень самой конечной точки тоже приходит узлом топологии, но правится он отдельно
	/// и в процентах. Узнаём его по диапазону: у точки и её узла он один и тот же.
	/// </summary>
	private static List<MicrophoneKnob> Without(List<MicrophoneKnob> knobs, (float Minimum, float Maximum)? range)
	{
		if (range is not { } endpoint)
		{
			return knobs;
		}

		var own = knobs.FindIndex(knob => knob.Kind == MicrophoneKnobKind.Level
			&& Math.Abs(knob.Minimum - endpoint.Minimum) < 0.01f
			&& Math.Abs(knob.Maximum - endpoint.Maximum) < 0.01f);

		if (own >= 0)
		{
			knobs.RemoveAt(own);
		}

		return knobs;
	}

	/// <summary>Пройти по всем узлам на пути сигнала этой точки.</summary>
	private static void Each(string endpointId, Action<IPart, int> visit, Action<string>? trace)
	{
		try
		{
			if (Activate<IDeviceTopology>(endpointId) is not { } topology)
			{
				trace?.Invoke("no topology");

				return;
			}

			var connectors = topology.GetConnectorCount();
			trace?.Invoke($"connectors: {connectors}");

			if (connectors == 0)
			{
				return;
			}

			topology.GetConnector(0, out var connector);

			// Разъём точки — только её край. Дальше топология уже другая, устройства,
			// и попасть в неё можно лишь перешагнув на разъём с той стороны.
			var hop = connector.GetConnectedTo(out var across);
			if (hop != 0 || across is not IPart start)
			{
				trace?.Invoke($"no hop to device: 0x{hop:X8}");

				return;
			}

			// Перешагнув, мы оказываемся на самом гнезде микрофона — то есть в начале пути,
			// а не в конце: узлы лежат ниже по течению, между гнездом и конечной точкой.
			Walk(start, visit, new HashSet<string>(StringComparer.Ordinal), depth: 0, trace);
		}
		catch (COMException exception)
		{
			// Устройство исчезло, пока мы шли к нему по топологии.
			trace?.Invoke($"walk broke off: {exception.Message}");
		}
	}

	private static void Walk(IPart part, Action<IPart, int> visit, HashSet<string> seen, int depth, Action<string>? trace)
	{
		// Смесители смыкают пути в кольца, а глубже десятка узлов топологии не бывает.
		if (depth > 10 || part.GetGlobalId(out var id) != 0 || !seen.Add(id))
		{
			return;
		}

		visit(part, depth);

		var next = part.EnumPartsOutgoing(out var outgoing);
		if (next != 0 || outgoing is null)
		{
			trace?.Invoke($"{new string(' ', depth * 2)}  end: 0x{next:X8}");

			return;
		}

		for (uint index = 0; index < outgoing.GetCount(); index++)
		{
			outgoing.GetPart(index, out var downstream);
			Walk(downstream, visit, seen, depth + 1, trace);
		}
	}

	private static void Inspect(IPart part, List<MicrophoneKnob> found, int depth, Action<string>? trace)
	{
		if (part.GetSubType(out var subtype) != 0)
		{
			return;
		}

		part.GetName(out var name);
		part.GetLocalId(out var local);
		part.GetPartType(out var kind);
		name = string.IsNullOrWhiteSpace(name) ? "" : name;

		trace?.Invoke($"{new string(' ', depth * 2)}{(kind == 0 ? "pin " : "node")} {local,-7} {subtype}  \"{name}\"");

		if (subtype == _volumeNode && Activate<IAudioVolumeLevel>(part) is { } level)
		{
			level.GetLevelRange(0, out var minimum, out var maximum, out var step);
			level.GetLevel(0, out var value);
			found.Add(new MicrophoneKnob(name, MicrophoneKnobKind.Level, local, minimum, maximum, step, value, On: true, level));
		}
		else if (subtype == _autoGainNode && Activate<IAudioAutoGainControl>(part) is { } gain)
		{
			gain.GetEnabled(out var enabled);
			found.Add(new MicrophoneKnob(name, MicrophoneKnobKind.AutoGain, local, 0, 0, 0, 0, enabled != 0, gain));
		}
	}

	private static T? Activate<T>(string endpointId) where T : class
	{
		var iid = typeof(T).GUID;
		var device = ((IMMDeviceEnumerator)new MMDeviceEnumeratorComObject()).GetDevice(endpointId);

		return device.Activate(ref iid, _clsCtxAll, IntPtr.Zero, out var raw) == 0 ? raw as T : null;
	}

	private static T? Activate<T>(IPart part) where T : class
	{
		var iid = typeof(T).GUID;

		return part.Activate(_clsCtxAll, ref iid, out var raw) == 0 ? raw as T : null;
	}

	[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDeviceTopology
	{
		uint GetConnectorCount();
		void GetConnector(uint index, out IConnector connector);
		// Дальше подузлы и поиск по номеру — идём разъёмом, они не нужны.
	}

	[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IConnector
	{
		void NotImpl_GetType();
		void NotImpl_GetDataFlow();
		void NotImpl_ConnectTo();
		void NotImpl_Disconnect();
		void NotImpl_IsConnected();

		[PreserveSig]
		int GetConnectedTo([MarshalAs(UnmanagedType.IUnknown)] out object connector);
	}

	[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPart
	{
		[PreserveSig]
		int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);

		[PreserveSig]
		int GetLocalId(out uint id);

		[PreserveSig]
		int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);

		[PreserveSig]
		int GetPartType(out uint type);

		[PreserveSig]
		int GetSubType(out Guid subtype);

		[PreserveSig]
		int GetControlInterfaceCount(out uint count);

		[PreserveSig]
		int GetControlInterface(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object description);

		[PreserveSig]
		int EnumPartsIncoming(out IPartsList parts);

		[PreserveSig]
		int EnumPartsOutgoing(out IPartsList parts);

		[PreserveSig]
		int GetTopologyObject([MarshalAs(UnmanagedType.IUnknown)] out object topology);

		[PreserveSig]
		int Activate(uint clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
	}

	[ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPartsList
	{
		uint GetCount();

		void GetPart(uint index, out IPart part);
	}

	[ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioVolumeLevel
	{
		[PreserveSig]
		int GetChannelCount(out uint channels);

		[PreserveSig]
		int GetLevelRange(uint channel, out float minimum, out float maximum, out float step);

		[PreserveSig]
		int GetLevel(uint channel, out float decibels);

		[PreserveSig]
		int SetLevel(uint channel, float decibels, IntPtr context);

		[PreserveSig]
		int SetLevelUniform(float decibels, IntPtr context);
	}

	[ComImport, Guid("85401FD4-6DE4-4B9D-9869-2D6753A82F3C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioAutoGainControl
	{
		[PreserveSig]
		int GetEnabled(out int enabled);

		[PreserveSig]
		int SetEnabled(int enabled, IntPtr context);
	}
}
