using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioDirigent;

/// <summary>Почему у строки такой значок — от этого зависит и его вид, и подсказка.</summary>
internal enum RowMark
{
	None,
	Priority,
	Blocked,
	Undecided,
}

/// <summary>
/// Строка списка устройств. Хранит только смысл — имя, состояние, вид значка, — а цвета
/// подбирает разметка. Класть сюда кисти нельзя: при смене темы они остались бы прежними,
/// и список не перекрасился бы вместе со всем остальным.
/// </summary>
internal sealed class DeviceRow(AudioEndpoint device) : INotifyPropertyChanged
{
	private string _name = device.Name;
	private string _state = "";
	private string _badge = "";
	private string? _badgeHint;
	private string _level = "—";
	private string _levelHint = "";
	private RowMark _mark;
	private bool _active;
	private bool _current;
	private bool _pinned;

	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Устройство остаётся тем же, пока строка жива: список сверяется по его номеру.</summary>
	public AudioEndpoint Device { get; private set; } = device;

	public string Name { get => _name; set => Set(ref _name, value); }

	public string State { get => _state; set => Set(ref _state, value); }

	public string Badge { get => _badge; set => Set(ref _badge, value); }

	public string? BadgeHint { get => _badgeHint; set => Set(ref _badgeHint, value); }

	public string Level { get => _level; set => Set(ref _level, value); }

	public string LevelHint { get => _levelHint; set => Set(ref _levelHint, value); }

	public RowMark Mark { get => _mark; set => Set(ref _mark, value); }

	public bool Active { get => _active; set => Set(ref _active, value); }

	/// <summary>Это устройство звучит сейчас.</summary>
	public bool Current { get => _current; set => Set(ref _current, value); }

	/// <summary>Уровень громкости закреплён правилом.</summary>
	public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }

	/// <summary>Строка живёт, пока живёт устройство: обновляем её, а не создаём заново.</summary>
	public void Update(AudioEndpoint fresh)
	{
		Device = fresh;
		Name = fresh.Name;
	}

	private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
	{
		if (Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
	}
}
