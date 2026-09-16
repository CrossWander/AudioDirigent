using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AudioDirigent;

/// <summary>
/// Глобальные сочетания клавиш. Окна у программы может не быть вовсе — она живёт в трее,
/// поэтому сообщения принимает отдельное окно без вида (HWND_MESSAGE).
/// </summary>
internal sealed class Hotkeys : IDisposable
{
	private const int _wmHotkey = 0x0312;

	// HWND_MESSAGE: окно только для сообщений, без вида и без места на экране.
	private static readonly IntPtr _messageOnly = new(-3);

	private readonly HwndSource _sink;
	private readonly Dictionary<int, Action> _actions = [];

	public Hotkeys(Action<string, object?[]> log, params (string Combination, Action Action)[] hotkeys)
	{
		_sink = new HwndSource(new HwndSourceParameters(nameof(Hotkeys)) { ParentWindow = _messageOnly });
		_sink.AddHook(OnMessage);

		foreach (var (combination, action) in hotkeys)
		{
			Register(combination, action, log);
		}
	}

	public void Dispose()
	{
		foreach (var id in _actions.Keys)
		{
			UnregisterHotKey(_sink.Handle, id);
		}

		_sink.Dispose();
	}

	private void Register(string combination, Action action, Action<string, object?[]> log)
	{
		var (modifiers, key) = Parse(combination);
		if (key == 0)
		{
			log("langLogHotkeyBad", [combination]);
			return;
		}

		// Идентификатор нужен только чтобы отличать сочетания друг от друга.
		var id = _actions.Count + 1;

		// Сочетание может быть занято другой программой — тогда система откажет,
		// и единственное, что можно сделать, — сказать об этом в журнале.
		if (!RegisterHotKey(_sink.Handle, id, modifiers, key))
		{
			log("langLogHotkeyBusy", [combination]);
			return;
		}

		_actions[id] = action;
		log("langLogHotkeyReady", [combination]);
	}

	/// <summary>Разбор строки вида «Ctrl+Alt+P»; ключ 0 — разобрать не удалось.</summary>
	private static (uint Modifiers, uint Key) Parse(string combination)
	{
		uint modifiers = 0;
		uint key = 0;

		foreach (var part in combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			switch (part.ToLowerInvariant())
			{
				case "alt":
					modifiers |= 0x1;
					break;
				case "ctrl" or "control":
					modifiers |= 0x2;
					break;
				case "shift":
					modifiers |= 0x4;
					break;
				case "win":
					modifiers |= 0x8;
					break;
				default:
					// Имена клавиш берём у WPF: своя таблица кодов тут была бы лишней.
					key = Enum.TryParse<Key>(part, ignoreCase: true, out var parsed)
						? (uint)KeyInterop.VirtualKeyFromKey(parsed)
						: 0;
					break;
			}
		}

		return (modifiers, modifiers == 0 ? 0 : key);
	}

	private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message == _wmHotkey && _actions.TryGetValue((int)wParam, out var action))
		{
			handled = true;
			action();
		}

		return IntPtr.Zero;
	}

	[DllImport("user32.dll")]
	private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

	[DllImport("user32.dll")]
	private static extern bool UnregisterHotKey(IntPtr window, int id);
}
