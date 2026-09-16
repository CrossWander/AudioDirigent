using System.Collections.Generic;
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
	private bool _expanded;
	private bool _capture;
	private bool _bare;
	private string _levelName = "";
	private string? _shared;
	private double _volume;
	private double _peak;
	private bool _hold;
	private IReadOnlyList<KnobRow> _knobs = [];

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

	/// <summary>Строка раскрыта: под ней показаны её настройки.</summary>
	public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

	/// <summary>Устройство записи: у него бывают и полоска сигнала, и ручки железа.</summary>
	public bool Capture { get => _capture; set => Set(ref _capture, value); }

	/// <summary>Кроме уровня устройство ничего не отдаёт — молчать об этом нельзя.</summary>
	public bool Bare { get => _bare; set => Set(ref _bare, value); }

	/// <summary>У вывода это громкость, у записи то же число Windows зовёт чувствительностью.</summary>
	public string LevelName { get => _levelName; set => Set(ref _levelName, value); }

	/// <summary>Правило уровня ловит не только это устройство; null — ловит только его.</summary>
	public string? Shared { get => _shared; set => Set(ref _shared, value); }

	public double Volume { get => _volume; set => Set(ref _volume, value); }

	/// <summary>Пик сигнала в процентах — им растёт полоска под ползунком.</summary>
	public double Peak { get => _peak; set => Set(ref _peak, value); }

	/// <summary>Удерживать уровень: правило существует ровно тогда, когда это включено.</summary>
	public bool Hold { get => _hold; set => Set(ref _hold, value); }

	/// <summary>Ручки, которые отдало само устройство: усиление, автоподстройка.</summary>
	public IReadOnlyList<KnobRow> Knobs { get => _knobs; set => Set(ref _knobs, value); }

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
