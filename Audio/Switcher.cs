using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AudioDirigent;

/// <summary>
/// Правило выбора устройства: события Core Audio плюс, если он настроен, опрос приёмника
/// по HID. Одним и тем же событием ловится и воткнутый провод, и подключённая гарнитура
/// Bluetooth — Windows заводит и то, и другое как появление эндпоинта.
/// </summary>
internal sealed class Switcher : IDisposable
{
	private static readonly TimeSpan _probeInterval = TimeSpan.FromSeconds(3);

	// USB-устройства возвращаются не сразу и вразнобой: даём системе досчитать их сама,
	// и только если устройство так и не появилось, лезем восстанавливать его руками.
	private static readonly TimeSpan _resumeSettle = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan _resumeRetry = TimeSpan.FromSeconds(10);
	private const int _resumeAttempts = 4;

	// Правило считается и из потока таймера, и из COM-колбэка, и из окна.
	private readonly Lock _gate = new();
	private readonly Timer _debounce;
	private readonly Timer _probePoll;
	private readonly Timer _resume;

	// Узлы PnP устройств, которые были живы в этом запуске: починке нужно знать, что
	// именно пропало, а Windows держит в списке и то, что унесли месяц назад.
	private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

	private Settings _settings = Settings.Load();
	private Dictionary<string, AudioEndpoint>? _seen;
	private IDisposable? _subscription;
	private DeviceProbe? _probe;
	private List<string> _beforeSleep = [];
	private int _resumeAttempt;
	private int _recovering;

	public Switcher()
	{
		_debounce = new Timer(_ => Safe(Reapply), null, Timeout.Infinite, Timeout.Infinite);
		_probePoll = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
		_resume = new Timer(_ => Safe(Resume), null, Timeout.Infinite, Timeout.Infinite);

		SystemEvents.PowerModeChanged += OnPowerModeChanged;
	}

	/// <summary>Событие журнала — для окна. Приходит из произвольного потока.</summary>
	public event Action<LogEntry>? Logged;

	/// <summary>Состав или состояние устройств изменились — окну пора перерисоваться.</summary>
	public event Action? DevicesChanged;

	/// <summary>Устройство появилось; второй аргумент — оно же стало устройством по умолчанию.</summary>
	public event Action<AudioEndpoint, bool>? Arrived;

	/// <summary>Устройство пропало.</summary>
	public event Action<AudioEndpoint>? Left;

	public bool Paused { get; set; }

	/// <summary>Приёмник найден и опрашивается по HID.</summary>
	public bool HasProbe => _probe is not null;

	/// <summary>Что говорит приёмник об устройстве: null — приёмника нет либо он не ответил.</summary>
	public bool? DeviceOn { get; private set; }

	/// <summary>Текущие правила: файл читается при загрузке и при сохранении, а не на каждый запрос.</summary>
	public Settings Rules
	{
		get
		{
			lock (_gate)
			{
				return _settings;
			}
		}
	}

	public void Start()
	{
		_subscription ??= Audio.Subscribe(new Watcher(OnAudioEvent));
		EnsureProbe();
		Apply();
	}

	/// <summary>Сохранить новые правила и сразу их применить.</summary>
	public void Save(Settings settings)
	{
		lock (_gate)
		{
			settings.Save();
			_settings = settings;
		}

		Apply();
	}

	/// <summary>Пересчитать правило и, если нужно, сменить устройство по умолчанию.</summary>
	public void Apply()
	{
		List<AudioEndpoint> arrived = [];
		List<AudioEndpoint> left = [];

		lock (_gate)
		{
			var now = Live().ToDictionary(device => device.Id);

			// Первый проход — не появление устройств, а знакомство с ними: иначе при
			// запуске программа объявляла бы о каждом устройстве в системе.
			if (_seen is { } before)
			{
				arrived = [.. now.Values.Where(device => !before.ContainsKey(device.Id))];
				left = [.. before.Values.Where(device => !now.ContainsKey(device.Id))];
			}

			_seen = now;
			_known.UnionWith(now.Values.Select(device => device.Node).OfType<string>());

			if (!Paused)
			{
				Adopt(arrived);

				// Направления считаются одним правилом: устройство приносит с собой и
				// наушники, и микрофон, а моно-канал Bluetooth портит именно их пару.
				SwitchIfNeeded(EDataFlow.Render, _settings.Output);
				SwitchIfNeeded(EDataFlow.Capture, _settings.Input);
			}
		}

		// Окно обновляется и на паузе: устройства всё равно появляются и исчезают.
		DevicesChanged?.Invoke();
		Announce(arrived, left);
	}

	public void Log(string key, params object?[] arguments) => Logged?.Invoke(Journal.Add(key, arguments));

	public void Dispose()
	{
		SystemEvents.PowerModeChanged -= OnPowerModeChanged;
		_debounce.Dispose();
		_probePoll.Dispose();
		_resume.Dispose();
		_subscription?.Dispose();
		_probe?.Dispose();
	}

	/// <summary>Вернуть в систему устройства, которые из неё пропали. Занимает секунды.</summary>
	public void Recover() => Recover(Missing());

	/// <summary>
	/// Узлы PnP устройств, которых система недосчиталась: тех, что были живы в этом запуске,
	/// и тех, чьи конечные точки Windows помнит, а самих устройств уже нет. Второе нужно,
	/// когда починку зовут из свежего запуска: звук отвалился до того, как программа успела
	/// на что-то посмотреть. Узлы, которых нет и в PnP, отсеет сама починка — их не перезапустить.
	/// </summary>
	public List<string> Missing()
	{
		var live = LiveNodes();
		var gone = Audio.ListDevices(EDataFlow.Render, DeviceState.NotPresent)
			.Concat(Audio.ListDevices(EDataFlow.Capture, DeviceState.NotPresent))
			.Select(device => device.Node)
			.OfType<string>()
			.ToList();

		lock (_gate)
		{
			gone.AddRange(_known);
		}

		return [.. gone.Where(node => !live.Contains(node)).Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>Устройство скрыто приёмником: он сообщает, что железо сейчас выключено.</summary>
	public bool Hidden(AudioEndpoint device) => _probe is { } probe && DeviceOn == false && probe.Covers(device);

	private static IEnumerable<AudioEndpoint> Live() =>
		Audio.ListDevices(EDataFlow.Render, DeviceState.Active)
			.Concat(Audio.ListDevices(EDataFlow.Capture, DeviceState.Active));

	private static HashSet<string> LiveNodes() =>
		Live().Select(device => device.Node).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

	/// <summary>Все перечисленные узлы вернулись в систему.</summary>
	private static bool Restored(IReadOnlyCollection<string> nodes)
	{
		var live = LiveNodes();

		return nodes.All(live.Contains);
	}

	private void Recover(List<string> nodes)
	{
		// Починку запускают и из окна, и из меню трея, а блокируют они каждый свою кнопку.
		// Вторая попытка поверх первой перезапускала бы службу звука дважды.
		if (Interlocked.Exchange(ref _recovering, 1) == 1)
		{
			return;
		}

		Safe(() =>
		{
			Recovery.Run(Log, nodes, Restored);
			Resubscribe();
			Apply();
		});

		Interlocked.Exchange(ref _recovering, 0);
	}

	/// <summary>
	/// Устройство, которое воткнули рукой, звучит сразу. Правила при этом не трогаются:
	/// для программы это то же самое, что выбор устройства вручную, а его она уважает и
	/// не перебивает. Устройство, уже описанное правилами, обходим — там решает правило.
	/// </summary>
	private void Adopt(List<AudioEndpoint> arrived)
	{
		foreach (var device in arrived.Where(device => device is { Plugged: true, HandsFree: false, Software: false }))
		{
			var config = _settings.For(device.Flow);
			if (config.Rank(device) >= 0 || config.Blocks(device) || Hidden(device))
			{
				continue;
			}

			Audio.SetDefault(device.Id);
			Log(device.Flow == EDataFlow.Capture ? "langLogAdoptedIn" : "langLogAdopted", device.Name);
			Volume.Apply(device, Log);
		}
	}

	private void SwitchIfNeeded(EDataFlow flow, Config config)
	{
		var active = Audio.ListDevices(flow, DeviceState.Active).Where(device => !Hidden(device)).ToList();
		if (config.SelectBest(active) is not { } best)
		{
			return;
		}

		var current = Audio.GetDefault(flow, ERole.Multimedia);
		if (current?.Id == best.Id)
		{
			return;
		}

		// Вмешиваемся только если текущее устройство непригодно, отсутствует или стоит
		// ниже по приоритету. Ручной выбор постороннего устройства уважаем.
		var currentRank = current is null ? -1 : config.Rank(current);
		if (current is not null
			&& !config.Blocks(current)
			&& !Hidden(current)
			&& (currentRank < 0 || currentRank <= config.Rank(best)))
		{
			return;
		}

		Audio.SetDefault(best.Id);

		// Отдельный ключ вместо подстановки «(нет)»: имя устройства — аргумент, а он
		// сохраняется в файл как есть и на другом языке уже не переведётся.
		if (current is null)
		{
			Log(flow == EDataFlow.Capture ? "langLogSwitchedInFromNone" : "langLogSwitchedFromNone", best.Name);
		}
		else
		{
			Log(flow == EDataFlow.Capture ? "langLogSwitchedIn" : "langLogSwitched", current.Name, best.Name);
		}

		Volume.Apply(best, Log);
	}

	/// <summary>Сообщить окну о том, что подключилось и что пропало, — по одному событию на устройство.</summary>
	private void Announce(List<AudioEndpoint> arrived, List<AudioEndpoint> left)
	{
		if (Pick(arrived) is { } came)
		{
			var current = Audio.GetDefault(came.Flow, ERole.Multimedia);
			Arrived?.Invoke(came, current?.Id == came.Id);
		}

		if (Pick(left) is { } gone)
		{
			Left?.Invoke(gone);
		}
	}

	/// <summary>
	/// Гарнитура приходит в систему сразу несколькими устройствами — наушниками, микрофоном,
	/// телефонным профилем. Человеку это одно событие, поэтому берём то из них, что слышно.
	/// </summary>
	private static AudioEndpoint? Pick(List<AudioEndpoint> devices) =>
		devices
			.OrderBy(device => device.Flow == EDataFlow.Render ? 0 : 1)
			.ThenBy(device => device.HandsFree ? 1 : 0)
			.FirstOrDefault();

	// Windows шлёт события пачками: подключение одного устройства — это несколько
	// уведомлений подряд, и без задержки правило считалось бы на каждое.
	private void OnAudioEvent() => _debounce.Change(800, Timeout.Infinite);

	// Исключение с потока пула никто не поймает, а программа живёт в трее часами. Сюда же
	// сходятся ошибки починки: окну и трею незачем знать, каким ключом их записывать.
	private void Safe(Action action)
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			Log("langLogSwitchError", exception.Message);
		}
	}

	private void Reapply()
	{
		// Приёмник могли воткнуть уже после запуска — программу поднимает автозапуск.
		// Его появление это тоже событие Core Audio, так что ищем здесь, а не по таймеру.
		EnsureProbe();
		Apply();
	}

	// Устройства не переживают гибернацию: система усыпляет USB-порт, а обратно они
	// возвращаются не всегда. Событий Core Audio при этом может не быть вовсе, поэтому
	// после пробуждения пересчитываем правило сами.
	private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
	{
		if (e.Mode == PowerModes.Suspend)
		{
			// Чинить нечего, если устройства не было и до сна: его просто не подключали.
			_beforeSleep = [.. Live().Select(device => device.Node).OfType<string>().Distinct()];
			Log("langLogSuspend");
			return;
		}

		if (e.Mode != PowerModes.Resume)
		{
			return;
		}

		Log("langLogResume");

		// Дескриптор приёмника сон не переживает: устройство переоткроется заново.
		DropProbe();

		_resumeAttempt = 0;
		_resume.Change(_resumeSettle, Timeout.InfiniteTimeSpan);
	}

	private void Resume()
	{
		Reapply();

		var live = LiveNodes();
		var missing = _beforeSleep.Where(node => !live.Contains(node)).ToList();
		if (missing.Count == 0)
		{
			return;
		}

		if (++_resumeAttempt < _resumeAttempts)
		{
			_resume.Change(_resumeRetry, Timeout.InfiniteTimeSpan);
			return;
		}

		Log("langLogDeviceLost", missing.Count);

		// Сюда доходят только после того, как устройство не вернулось само, — случай,
		// в котором раньше спасала лишь перезагрузка.
		Recover(missing);
	}

	// Перезапуск службы звука обрывает подписку: уведомитель зарегистрирован в её
	// уже мёртвом экземпляре, и события оттуда больше не придут.
	private void Resubscribe()
	{
		var previous = _subscription;
		_subscription = Audio.Subscribe(new Watcher(OnAudioEvent));

		try
		{
			previous?.Dispose();
		}
		catch (COMException)
		{
			// служба перезапустилась — отписываться уже не от чего
		}
	}

	private void Poll()
	{
		Safe(PollProbe);

		try
		{
			// Таймер одноразовый и перезаводится отсюда: периодический успел бы уйти
			// вторым опросом в тот же поток устройства, пока первый ещё ждёт ответа.
			_probePoll.Change(_probeInterval, Timeout.InfiniteTimeSpan);
		}
		catch (ObjectDisposedException)
		{
			// программа закрывается
		}
	}

	// Приёмник беспроводной гарнитуры остаётся активным аудиоустройством и с выключенной
	// гарнитурой — состояние Core Audio об этом молчит, спрашиваем приёмник напрямую.
	// Средствами Windows это не выясняется никак, поэтому опрос и существует.
	private void EnsureProbe()
	{
		if (_probe is not null || DeviceProbe.Open() is not { } probe)
		{
			return;
		}

		lock (_gate)
		{
			// Пока шло перечисление HID, устройство мог открыть другой поток.
			if (_probe is not null)
			{
				probe.Dispose();
				return;
			}

			_probe = probe;
		}

		Log("langLogProbeFound", $"{probe.ProductId:X4}", _probeInterval.TotalSeconds);
		_probePoll.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
	}

	private void DropProbe()
	{
		DeviceProbe? probe;
		lock (_gate)
		{
			(probe, _probe) = (_probe, null);
		}

		DeviceOn = null;
		probe?.Dispose();
	}

	private void PollProbe()
	{
		if (_probe is not { } probe)
		{
			return;
		}

		var state = probe.IsOn();
		if (state == DeviceOn)
		{
			return;
		}

		DeviceOn = state;
		Log(state switch
		{
			true => "langLogProbeOn",
			false => "langLogProbeOff",
			null => "langLogProbeSilent",
		});

		// Молчит — скорее всего приёмник вынули, и этот дескриптор уже не оживёт.
		// Закрываем; новый откроется на ближайшем событии Core Audio.
		if (state is null)
		{
			DropProbe();
		}

		Apply();
	}

	private sealed class Watcher(Action onChange) : IMMNotificationClient
	{
		public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChange();
		public void OnDeviceAdded(string deviceId) => onChange();
		public void OnDeviceRemoved(string deviceId) => onChange();
		public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId) => onChange();
		public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
	}
}
