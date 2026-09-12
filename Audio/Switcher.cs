using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AudioDirigent;

/// <summary>Правило выбора устройства вывода: события Core Audio плюс опрос донгла, если он есть.</summary>
internal sealed class Switcher : IDisposable
{
	private static readonly TimeSpan _donglePollInterval = TimeSpan.FromSeconds(3);

	// USB-устройства возвращаются не сразу и вразнобой: даём системе досчитать их сама,
	// и только если гарнитура так и не появилась, лезем восстанавливать её руками.
	private static readonly TimeSpan _resumeSettle = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan _resumeRetry = TimeSpan.FromSeconds(10);
	private const int _resumeAttempts = 4;

	// Правило считается и из потока таймера, и из COM-колбэка, и из окна.
	private readonly Lock _gate = new();
	private readonly Timer _debounce;
	private readonly Timer _donglePoll;
	private readonly Timer _resume;
	private Settings _settings = Settings.Load();
	private IDisposable? _subscription;
	private HeadsetProbe? _probe;
	private bool _headsetBeforeSleep;
	private int _resumeAttempt;
	private int _recovering;

	public Switcher()
	{
		_debounce = new Timer(_ => Safe(Reapply), null, Timeout.Infinite, Timeout.Infinite);
		_donglePoll = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
		_resume = new Timer(_ => Safe(Resume), null, Timeout.Infinite, Timeout.Infinite);

		SystemEvents.PowerModeChanged += OnPowerModeChanged;
	}

	/// <summary>Событие журнала — для окна. Приходит из произвольного потока.</summary>
	public event Action<LogEntry>? Logged;

	/// <summary>Состав или состояние устройств изменились — окну пора перерисоваться.</summary>
	public event Action? DevicesChanged;

	public bool Paused { get; set; }

	/// <summary>Донгл найден и опрашивается по HID.</summary>
	public bool HasDongle => _probe is not null;

	/// <summary>Состояние гарнитуры по данным донгла: null — донгла нет либо он не ответил.</summary>
	public bool? HeadsetOn { get; private set; }

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
		lock (_gate)
		{
			if (!Paused)
			{
				// Направления считаются одним правилом: гарнитура приносит с собой и
				// наушники, и микрофон, а моно-канал Bluetooth портит именно их пару.
				SwitchIfNeeded(EDataFlow.Render, _settings.Output);
				SwitchIfNeeded(EDataFlow.Capture, _settings.Input);
			}
		}

		// Окно обновляется и на паузе: устройства всё равно появляются и исчезают.
		DevicesChanged?.Invoke();
	}

	public void Log(string key, params object?[] arguments) => Logged?.Invoke(Journal.Add(key, arguments));

	public void Dispose()
	{
		SystemEvents.PowerModeChanged -= OnPowerModeChanged;
		_debounce.Dispose();
		_donglePoll.Dispose();
		_resume.Dispose();
		_subscription?.Dispose();
		_probe?.Dispose();
	}

	/// <summary>Вернуть пропавшую гарнитуру в систему и пересчитать правило. Занимает секунды.</summary>
	public void Recover(bool withNgenuity)
	{
		// Починку запускают и из окна, и из меню трея, а блокируют они каждый свою кнопку.
		// Вторая попытка поверх первой перезапускала бы службу звука и NGENUITY дважды.
		if (Interlocked.Exchange(ref _recovering, 1) == 1)
		{
			return;
		}

		Safe(() =>
		{
			Recovery.Run(Log, HeadsetPresent, withNgenuity);
			Resubscribe();
			Apply();
		});

		Interlocked.Exchange(ref _recovering, 0);
	}

	/// <summary>Гарнитура на месте целиком — и наушники, и микрофон.</summary>
	public bool HeadsetPresent() => Rules.HeadsetPresent();

	private void SwitchIfNeeded(EDataFlow flow, Config config)
	{
		var headsetOff = _probe is not null && HeadsetOn == false;
		if (config.SelectBest(Audio.ListDevices(flow, DeviceState.Active), headsetOff) is not { } best)
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
		if (current is not null && !config.Unusable(current, headsetOff) && (currentRank < 0 || currentRank <= config.Rank(best)))
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
		// Донгл могли воткнуть уже после запуска — программу поднимает автозапуск.
		// Его появление это тоже событие Core Audio, так что ищем здесь, а не по таймеру.
		EnsureProbe();
		Apply();
	}

	// Гарнитура не переживает гибернацию: система усыпляет USB-порт, а обратно устройство
	// возвращается не всегда. Событий Core Audio при этом может не быть вовсе, поэтому
	// после пробуждения пересчитываем правило сами.
	private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
	{
		if (e.Mode == PowerModes.Suspend)
		{
			// Чинить нечего, если гарнитуры не было и до сна — её просто не подключали.
			_headsetBeforeSleep = HeadsetPresent();
			Log("langLogSuspend");
			return;
		}

		if (e.Mode != PowerModes.Resume)
		{
			return;
		}

		Log("langLogResume");

		// Дескриптор донгла сон не переживает: устройство переоткроется заново.
		DropProbe();

		_resumeAttempt = 0;
		_resume.Change(_resumeSettle, Timeout.InfiniteTimeSpan);
	}

	private void Resume()
	{
		Reapply();

		if (!_headsetBeforeSleep || HeadsetPresent())
		{
			return;
		}

		if (++_resumeAttempt < _resumeAttempts)
		{
			_resume.Change(_resumeRetry, Timeout.InfiniteTimeSpan);
			return;
		}

		Log("langLogHeadsetLost");

		// Сюда доходят только после того, как гарнитура не вернулась сама, — случай,
		// в котором раньше спасала лишь перезагрузка. Чиним по полной; если NGENUITY
		// не запущен, этот шаг сам собой превратится в обычную переустановку.
		Recover(withNgenuity: true);
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
		Safe(PollDongle);

		try
		{
			// Таймер одноразовый и перезаводится отсюда: периодический успел бы уйти
			// вторым опросом в тот же поток донгла, пока первый ещё ждёт ответа.
			_donglePoll.Change(_donglePollInterval, Timeout.InfiniteTimeSpan);
		}
		catch (ObjectDisposedException)
		{
			// программа закрывается
		}
	}

	// Донгл беспроводных моделей остаётся активным аудиоустройством и с выключенной
	// гарнитурой — состояние Core Audio об этом молчит, спрашиваем донгл напрямую.
	private void EnsureProbe()
	{
		if (_probe is not null || HeadsetProbe.Open() is not { } probe)
		{
			return;
		}

		lock (_gate)
		{
			// Пока шло перечисление HID, донгл мог открыть другой поток.
			if (_probe is not null)
			{
				probe.Dispose();
				return;
			}

			_probe = probe;
		}

		Log("langLogDongleFound", $"{probe.ProductId:X4}", _donglePollInterval.TotalSeconds);
		_donglePoll.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
	}

	private void DropProbe()
	{
		HeadsetProbe? probe;
		lock (_gate)
		{
			(probe, _probe) = (_probe, null);
		}

		HeadsetOn = null;
		probe?.Dispose();
	}

	private void PollDongle()
	{
		if (_probe is not { } probe)
		{
			return;
		}

		var state = probe.IsHeadsetOn();
		if (state == HeadsetOn)
		{
			return;
		}

		HeadsetOn = state;
		Log(state switch
		{
			true => "langLogDongleOn",
			false => "langLogDongleOff",
			null => "langLogDongleSilent",
		});

		// Молчит — скорее всего донгл вынули, и этот дескриптор уже не оживёт.
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
