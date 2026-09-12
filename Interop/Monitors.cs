using System;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Границы монитора. Нужны развёрнутому окну без системной рамки: по умолчанию оно
/// занимает весь экран и накрывает панель задач, а Windows спрашивает предельный размер
/// сообщением WM_GETMINMAXINFO.
/// </summary>
internal static class Monitors
{
	private const int _wmGetMinMaxInfo = 0x0024;
	private const int _monitorDefaultToNearest = 0x2;

	/// <summary>
	/// Обработчик окна: на запрос предельного размера отдаёт рабочую область монитора.
	/// </summary>
	public static IntPtr LimitMaximizedSize(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message != _wmGetMinMaxInfo)
		{
			return IntPtr.Zero;
		}

		var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(MonitorFromWindow(window, _monitorDefaultToNearest), ref monitorInfo))
		{
			return IntPtr.Zero;
		}

		var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
		limits.MaxPosition = new NativePoint
		{
			X = monitorInfo.Work.Left - monitorInfo.Monitor.Left,
			Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top,
		};
		limits.MaxSize = new NativePoint
		{
			X = monitorInfo.Work.Right - monitorInfo.Work.Left,
			Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top,
		};
		limits.MaxTrackSize = limits.MaxSize;

		Marshal.StructureToPtr(limits, lParam, fDeleteOld: true);
		handled = true;

		return IntPtr.Zero;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MinMaxInfo
	{
		public NativePoint Reserved;
		public NativePoint MaxSize;
		public NativePoint MaxPosition;
		public NativePoint MinTrackSize;
		public NativePoint MaxTrackSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public NativeRect Monitor;
		public NativeRect Work;
		public int Flags;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
