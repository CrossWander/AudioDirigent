using System.Collections.Generic;

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
internal sealed class DeviceRow(AudioEndpoint device) : Notifier
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
	private bool _inRule;
	private bool _canRaise;
	private bool _canLower;
	private bool _canMakeMain;
	private bool _canLink;
	private string _linkText = "";
	private bool _expanded;
	private bool _capture;
	private bool _bare;
	private string _levelName = "";
	private string? _shared;
	private double _volume;
	private double _peak;
	private string _peakText = "";
	private string _charge = "";
	private bool _canBind;
	private bool _pairing;
	private string _bindNote = "";
	private bool _testing;
	private string _verdict = "";
	private bool? _verdictGood;
	private bool _hold;
	private IReadOnlyList<KnobRow> _knobs = [];

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

	/// <summary>Устройство стоит в правиле: только у такого есть место в очереди.</summary>
	public bool InRule { get => _inRule; set => Set(ref _inRule, value); }

	/// <summary>Есть куда двигать вверх; у первого в правиле стрелка гаснет.</summary>
	public bool CanRaise { get => _canRaise; set => Set(ref _canRaise, value); }

	public bool CanLower { get => _canLower; set => Set(ref _canLower, value); }

	/// <summary>Живое устройство без правила: ему и предлагают стать основным.</summary>
	public bool CanMakeMain { get => _canMakeMain; set => Set(ref _canMakeMain, value); }

	/// <summary>Связь можно поднять или разорвать — это умеет только Bluetooth.</summary>
	public bool CanLink { get => _canLink; set => Set(ref _canLink, value); }

	public string LinkText { get => _linkText; set => Set(ref _linkText, value); }

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

	/// <summary>Тот же пик в децибелах: на слух «тихо» и «нормально» неотличимы.</summary>
	public string PeakText { get => _peakText; set => Set(ref _peakText, value); }

	/// <summary>Заряд с процентом; пусто — устройство его не сообщает.</summary>
	public string Charge { get => _charge; set => Set(ref _charge, value); }

	/// <summary>Заряда нет, но устройство по радио: возможно, оно кричит его в эфир.</summary>
	public bool CanBind { get => _canBind; set => Set(ref _canBind, value); }

	/// <summary>Идёт привязка: кнопку на эти секунды надо погасить.</summary>
	public bool Pairing { get => _pairing; set => Set(ref _pairing, value); }

	/// <summary>Чем кончилась привязка. Пусто — её ещё не запускали.</summary>
	public string BindNote { get => _bindNote; set => Set(ref _bindNote, value); }

	/// <summary>Идёт проверка: кнопку на это время надо погасить.</summary>
	public bool Testing { get => _testing; set => Set(ref _testing, value); }

	/// <summary>Что показала проверка. Пусто — её ещё не запускали.</summary>
	public string Verdict { get => _verdict; set => Set(ref _verdict, value); }

	/// <summary>Приговор хороший, плохой или никакой: им красится строка.</summary>
	public bool? VerdictGood { get => _verdictGood; set => Set(ref _verdictGood, value); }

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

}
