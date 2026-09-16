using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioDirigent;

/// <summary>
/// Всплывающее уведомление трея через Shell_NotifyIcon напрямую. WinForms умеет показывать
/// его только со звуком, а пока висит предыдущее уведомление, шелл новое молча проглатывает.
/// Здесь старое гасится перед показом нового, и звука нет: смена устройства — не повод шуметь.
/// </summary>
internal static class Balloon
{
	private const int _modify = 0x00000001;
	private const int _info = 0x00000010;
	private const int _noSound = 0x00000010;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public int Size;
		public IntPtr Window;
		public uint Id;
		public int Flags;
		public int CallbackMessage;
		public IntPtr Icon;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string Tip;

		public int State;
		public int StateMask;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string Info;

		public int Timeout;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string InfoTitle;

		public int InfoFlags;
		public Guid Item;
		public IntPtr BalloonIcon;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

	/// <summary>Тихий путь доступен: поля WinForms нашлись и значок уже в трее.</summary>
	public static bool Ready(NotifyIcon icon) => Target(icon) is not null;

	/// <summary>Показать уведомление тихо; false — не вышло, остаётся обычный ShowBalloonTip.</summary>
	public static bool Show(NotifyIcon icon, string title, string text)
	{
		if (Target(icon) is not { } target)
		{
			return false;
		}

		// Пустой текст убирает предыдущее уведомление. Без этого второе подряд не приходит:
		// шелл показывает по одному на значок и новое просто игнорирует.
		Send(target, "", "");

		return Send(target, title, text);
	}

	private static bool Send((IntPtr Window, uint Id) target, string title, string text)
	{
		var data = new NotifyIconData
		{
			Size = Marshal.SizeOf<NotifyIconData>(),
			Window = target.Window,
			Id = target.Id,
			Flags = _info,
			Tip = "",
			Info = text,
			InfoTitle = title,
			InfoFlags = _noSound,
		};

		return Shell_NotifyIcon(_modify, ref data);
	}

	// Свой значок в трее WinForms держит при себе, а шеллу нужны именно его окно и номер.
	// Поля приватные, поэтому ищем их по типу и имени, а не по одному конкретному названию:
	// переименуют — просто вернём null, и уведомление уйдёт обычным путём.
	private static (IntPtr Window, uint Id)? Target(NotifyIcon icon)
	{
		var fields = typeof(NotifyIcon).GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

		var window = fields
			.FirstOrDefault(field => typeof(NativeWindow).IsAssignableFrom(field.FieldType))
			?.GetValue(icon) as NativeWindow;

		// Тип номера уже менялся: в .NET 10 это uint, раньше был int. Берём оба.
		var number = fields.FirstOrDefault(field => field.Name.Trim('_') == "id")?.GetValue(icon) switch
		{
			int value => (uint)value,
			uint value => value,
			_ => (uint?)null,
		};

		return window?.Handle is { } handle && handle != IntPtr.Zero && number is { } id
			? (handle, id)
			: null;
	}
}
