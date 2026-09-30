using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AudioDirigent;

/// <summary>
/// Скруглённые углы у окна без системной рамки. Windows 11 скругляет такие окна сама, а на
/// десятке — нет, и просить её об этом нечем: DWM узнал о скруглениях только в одиннадцатой.
/// Поэтому форму окна задаём сами, областью: она работает и там, и там, и переживает
/// WindowChrome, которым держится перетаскивание и прилипание к краям.
///
/// Прозрачности окна для этого не хватило бы: AllowsTransparency отключает аппаратный вывод
/// и ломает разворот на весь экран — цена за мягкий край слишком велика.
/// </summary>
internal static partial class Rounded
{
	private const int _radius = 8;

	public static void Follow(Window window, int radius = _radius)
	{
		window.SourceInitialized += (_, _) => Apply(window, radius);
		window.SizeChanged += (_, _) => Apply(window, radius);
		window.StateChanged += (_, _) => Apply(window, radius);
	}

	private static void Apply(Window window, int radius)
	{
		var handle = new WindowInteropHelper(window).Handle;

		if (handle == IntPtr.Zero)
		{
			return;
		}

		// Развёрнутое окно занимает экран целиком, и скруглять его нечем: под углами будет
		// не тень, а рабочий стол, просвечивающий четырьмя дырами.
		if (window.WindowState == WindowState.Maximized)
		{
			SetWindowRgn(handle, IntPtr.Zero, true);

			return;
		}

		if (!GetWindowRect(handle, out var rect))
		{
			return;
		}

		// Область живёт в настоящих точках экрана, а не в тех, которыми меряет WPF: на
		// мониторе со масштабом 150 % радиус в восемь единиц разметки — двенадцать точек.
		var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
		var corner = (int)Math.Round(radius * scale * 2);

		// Ширина области считается по краю включительно, отсюда лишняя единица.
		var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, corner, corner);

		// Дальше областью владеет окно: удалять её нам уже нельзя.
		if (!SetWindowRgn(handle, region, true))
		{
			DeleteObject(region);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left, Top, Right, Bottom;
	}

	[LibraryImport("gdi32.dll")]
	private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(IntPtr handle);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetWindowRect(IntPtr window, out RECT rect);
}
