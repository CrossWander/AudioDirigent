using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Радиомодуль Bluetooth: кого он помнит, кто рядом и как с ними знакомиться. Всё, что
/// касается уже подключённого звука, живёт в <see cref="BluetoothAudio"/> — там речь о
/// конечных точках Core Audio, а здесь о самих устройствах, ещё до всякого звука.
/// </summary>
internal static class Bluetooth
{
	// Длина имени в структуре задана заранее: устройство отдаёт его целиком или никак.
	private const int _nameLength = 248;

	// Опрос эфира считается кратностями 1.28 секунды, и дольше минуты его не ведут.
	private const double _tick = 1.28;

	private const int _authenticationUndefined = 0xFF;

	/// <summary>Помнит ли радиомодуль хоть что-то — и есть ли он вообще.</summary>
	public static bool Present
	{
		get
		{
			using var radios = Radios();

			return radios.Count > 0;
		}
	}

	/// <summary>
	/// Устройства, известные системе. Опрос эфира (<paramref name="inquiry"/>) находит и
	/// незнакомые, но платится за это эфиром: пока он идёт, наушники, играющие через тот же
	/// радиомодуль, захлёбываются. Поэтому он бывает только по кнопке и с предупреждением.
	/// </summary>
	public static List<BluetoothDevice> Devices(bool inquiry = false, double seconds = 6)
	{
		var found = new Dictionary<ulong, BluetoothDevice>();
		using var radios = Radios();

		foreach (var radio in radios.Handles)
		{
			var search = new SearchParams
			{
				Size = (uint)Marshal.SizeOf<SearchParams>(),
				ReturnAuthenticated = 1,
				ReturnRemembered = 1,
				ReturnConnected = 1,
				ReturnUnknown = inquiry ? 1 : 0,
				IssueInquiry = inquiry ? 1 : 0,
				TimeoutMultiplier = (byte)Math.Clamp((int)Math.Ceiling(seconds / _tick), 1, 48),
				Radio = radio,
			};

			var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
			var find = BluetoothFindFirstDevice(ref search, ref info);
			if (find == IntPtr.Zero)
			{
				continue;
			}

			try
			{
				do
				{
					// Один и тот же наушник виден с каждого радиомодуля: берём первую встречу.
					found.TryAdd(info.Address, new BluetoothDevice(
						info.Address,
						string.IsNullOrWhiteSpace(info.Name) ? "" : info.Name.Trim(),
						info.Class,
						info.Connected != 0,
						info.Authenticated != 0 || info.Remembered != 0));
				}
				while (BluetoothFindNextDevice(find, ref info));
			}
			finally
			{
				BluetoothFindDeviceClose(find);
			}
		}

		return [.. found.Values];
	}

	/// <summary>
	/// Познакомить систему с устройством. Диалог показывает Windows: у неё уже есть и
	/// сверка кода, и ввод пин-кода, и перевод на язык человека — своя пара окон вышла бы
	/// беднее. Возвращает код ошибки; 0 — устройство спарено.
	/// </summary>
	public static uint Pair(BluetoothDevice device, IntPtr owner)
	{
		using var radios = Radios();
		if (radios.Count == 0)
		{
			return uint.MaxValue;
		}

		var info = new DeviceInfo
		{
			Size = (uint)Marshal.SizeOf<DeviceInfo>(),
			Address = device.Address,
		};

		return BluetoothAuthenticateDeviceEx(owner, radios.Handles[0], ref info, IntPtr.Zero, _authenticationUndefined);
	}

	/// <summary>Забыть устройство. 0 — забыто.</summary>
	public static uint Forget(BluetoothDevice device)
	{
		var address = device.Address;

		return BluetoothRemoveDevice(ref address);
	}

	private static RadioSet Radios()
	{
		var handles = new List<IntPtr>();
		var parameters = new FindRadioParams { Size = (uint)Marshal.SizeOf<FindRadioParams>() };
		var find = BluetoothFindFirstRadio(ref parameters, out var radio);

		if (find != IntPtr.Zero)
		{
			do
			{
				handles.Add(radio);
			}
			while (BluetoothFindNextRadio(find, out radio));

			BluetoothFindRadioClose(find);
		}

		return new RadioSet(handles);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FindRadioParams
	{
		public uint Size;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SearchParams
	{
		public uint Size;
		public int ReturnAuthenticated;
		public int ReturnRemembered;
		public int ReturnUnknown;
		public int ReturnConnected;
		public int IssueInquiry;
		public byte TimeoutMultiplier;
		public IntPtr Radio;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemTime
	{
		public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DeviceInfo
	{
		public uint Size;
		public ulong Address;
		public uint Class;
		public int Connected;
		public int Remembered;
		public int Authenticated;
		public SystemTime LastSeen;
		public SystemTime LastUsed;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = _nameLength)]
		public string Name;
	}

	/// <summary>Дескрипторы радиомодулей: их закрывает тот, кто открыл, и всегда.</summary>
	private sealed class RadioSet(List<IntPtr> handles) : IDisposable
	{
		public List<IntPtr> Handles { get; } = handles;

		public int Count => Handles.Count;

		public void Dispose()
		{
			foreach (var handle in Handles)
			{
				CloseHandle(handle);
			}

			Handles.Clear();
		}
	}

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

	[DllImport("bthprops.cpl", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

	[DllImport("bthprops.cpl", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindRadioClose(IntPtr find);

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams parameters, ref DeviceInfo info);

	[DllImport("bthprops.cpl", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);

	[DllImport("bthprops.cpl", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindDeviceClose(IntPtr find);

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern uint BluetoothAuthenticateDeviceEx(
		IntPtr owner, IntPtr radio, ref DeviceInfo info, IntPtr outOfBand, int requirements);

	[DllImport("bthprops.cpl", SetLastError = true)]
	private static extern uint BluetoothRemoveDevice(ref ulong address);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
