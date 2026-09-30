using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioDirigent;

/// <summary>
/// Заряд из эфира. Часть гарнитур не сообщает его ни Windows, ни по проводу — только
/// кричит в объявлениях Bluetooth, которые слышит любой приёмник поблизости. Слушать их
/// умеет единственный механизм Windows, BluetoothLEAdvertisementWatcher, и он живёт в
/// WinRT: готовой обёртки к нему нет, пакет с обёрткой весит шесть мегабайт, поэтому
/// интерфейсы описаны здесь руками. Номера мест в таблицах вызовов сняты с метаданных
/// Windows (System32\WinMetadata), а не выписаны по памяти: там Start стоит до add_Received,
/// а не после, и порядок «на глаз» тихо ломает подписку.
///
/// Что именно читать из объявления, программа не знает — знает config.json. Здесь только
/// доставка байтов, разбор в <see cref="BeaconRule"/>.
/// </summary>
internal static unsafe partial class Beacon
{
	private const string _class = "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher";

	private static readonly Guid _unknown = new("00000000-0000-0000-C000-000000000046");
	private static readonly Guid _agile = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
	private static readonly Guid _factory = new("00000035-0000-0000-C000-000000000046");

	// Идентификатор обобщённого интерфейса считается хешем от сигнатуры типа — угадать его
	// нельзя, поэтому он снят с готовой проекции WinRT и записан сюда числом.
	private static readonly Guid _handler = new("90EB4ECA-D465-5EA0-A61C-033C8C5ECEF2");

	private static readonly Guid _watcher = new("A6AC336F-F3D3-4297-8D6C-C81EA6623F40");
	private static readonly Guid _received = new("27987DDF-E596-41BE-8D43-9E6731D4A913");
	private static readonly Guid _maker = new("912DBA18-6963-4533-B061-4694DAFB34E5");
	private static readonly Guid _access = new("905A0FEF-BC53-11DF-8C49-001E4FC686DA");

	// Места в таблицах вызовов. Первые шесть занимает IInspectable, свои методы идут с шестого.
	private const int _slotSignal = 6, _slotAddress = 7, _slotAdvertisement = 10;
	private const int _slotMakerData = 11;
	private const int _slotGetAt = 6, _slotSize = 7;
	private const int _slotCompany = 6, _slotData = 8;
	private const int _slotLength = 7, _slotBytes = 3;
	private const int _slotScanning = 12, _slotStart = 17, _slotStop = 18, _slotAdd = 19, _slotRemove = 20;
	private const int _slotActivate = 6;

	private const int _noInterface = unchecked((int)0x8000_4002);

	// Заряд меняется медленно, а гарнитуру уносят из комнаты быстро: показывать вчерашнее
	// число нечестно, показывать пустоту после каждой паузы в эфире — суетливо.
	private static readonly TimeSpan _stale = TimeSpan.FromMinutes(30);

	private static readonly Lock _guard = new();
	private static readonly Dictionary<string, (int Percent, DateTime Heard)> _heard = [];
	private static readonly Dictionary<string, Sighting> _sweep = [];

	private static readonly IntPtr _callback = Callback();
	private static Timer? _timer;

	/// <summary>Слушать эфир время от времени, как записано в настройках.</summary>
	public static void Begin()
	{
		if (Store.Current.Beacons.Count == 0 || Store.Current.BeaconMinutes <= 0)
		{
			return;
		}

		var every = TimeSpan.FromMinutes(Store.Current.BeaconMinutes);

		// Поле нужно только затем, чтобы таймер не собрали как мусор вместе с последней
		// ссылкой на него: программа живёт сутками, и сборщик успевает.
		_timer = new Timer(_ => Listen(Store.Current.BeaconSeconds), null, TimeSpan.Zero, every);
	}

	/// <summary>Заряд устройства с этим адресом; null — маяк к нему не привязан или давно молчит.</summary>
	public static int? Charge(string? mac)
	{
		if (string.IsNullOrEmpty(mac))
		{
			return null;
		}

		var key = Store.Current.BeaconBound.FirstOrDefault(pair => pair.Value == mac).Key;

		if (key is null)
		{
			return null;
		}

		lock (_guard)
		{
			return _heard.TryGetValue(key, out var seen) && DateTime.UtcNow - seen.Heard < _stale
				? seen.Percent
				: null;
		}
	}

	/// <summary>Послушать эфир прямо сейчас и вернуть всё услышанное — этим идёт привязка.</summary>
	public static IReadOnlyList<Sighting> Sweep(int seconds)
	{
		lock (_guard)
		{
			_sweep.Clear();
		}

		Listen(seconds);

		lock (_guard)
		{
			return [.. _sweep.Values.OrderByDescending(found => found.Signal)];
		}
	}

	private static void Listen(int seconds)
	{
		// Приёмник отвечает не на том потоке, на котором его завели, поэтому слушаем в
		// свободной квартире COM. Собственный поток, а не пул: в пуле пришлось бы верить,
		// что квартира там именно свободная.
		var worker = new Thread(() => Session(Math.Clamp(seconds, 1, 60))) { IsBackground = true };
		worker.SetApartmentState(ApartmentState.MTA);
		worker.Start();
		worker.Join(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60) + 10));
	}

	private static void Session(int seconds)
	{
		var watcher = Create();

		if (watcher == IntPtr.Zero)
		{
			return;
		}

		try
		{
			// Активное сканирование: приёмник переспрашивает устройство и получает второй
			// блок данных. Заряд лежит в первом, но без переспроса часть гарнитур молчит.
			if (Failed(((delegate* unmanaged<IntPtr, int, int>)Slot(watcher, _slotScanning))(watcher, 1)))
			{
				return;
			}

			long token;

			if (Failed(((delegate* unmanaged<IntPtr, IntPtr, long*, int>)Slot(watcher, _slotAdd))(watcher, _callback, &token))
				|| Failed(((delegate* unmanaged<IntPtr, int>)Slot(watcher, _slotStart))(watcher)))
			{
				return;
			}

			Thread.Sleep(TimeSpan.FromSeconds(seconds));

			((delegate* unmanaged<IntPtr, int>)Slot(watcher, _slotStop))(watcher);
			((delegate* unmanaged<IntPtr, long, int>)Slot(watcher, _slotRemove))(watcher, token);
		}
		catch (Exception exception)
		{
			Journal.Add("langLogBeaconFailed", exception.Message);
		}
		finally
		{
			Release(watcher);
		}
	}

	private static IntPtr Create()
	{
		if (Failed(WindowsCreateString(_class, (uint)_class.Length, out var name)))
		{
			return IntPtr.Zero;
		}

		try
		{
			// Радиомодуля может не быть вовсе, и это не сбой: на машине без Bluetooth
			// фабрика не поднимется, а программа должна работать дальше как ни в чём.
			if (Failed(RoGetActivationFactory(name, _factory, out var factory)) || factory == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}

			try
			{
				IntPtr instance;

				if (Failed(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(factory, _slotActivate))(factory, &instance)))
				{
					return IntPtr.Zero;
				}

				try
				{
					return Cast(instance, _watcher);
				}
				finally
				{
					Release(instance);
				}
			}
			finally
			{
				Release(factory);
			}
		}
		finally
		{
			WindowsDeleteString(name);
		}
	}

	/// <summary>Разбор одного объявления. Вызывается приёмником, а не нами.</summary>
	private static void Take(IntPtr args)
	{
		var info = Cast(args, _received);

		if (info == IntPtr.Zero)
		{
			return;
		}

		try
		{
			short signal;
			IntPtr advertisement;

			if (Failed(((delegate* unmanaged<IntPtr, short*, int>)Slot(info, _slotSignal))(info, &signal))
				|| Failed(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(info, _slotAdvertisement))(info, &advertisement))
				|| advertisement == IntPtr.Zero)
			{
				return;
			}

			try
			{
				Blocks(advertisement, signal);
			}
			finally
			{
				Release(advertisement);
			}
		}
		finally
		{
			Release(info);
		}
	}

	private static void Blocks(IntPtr advertisement, short signal)
	{
		IntPtr list;

		if (Failed(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(advertisement, _slotMakerData))(advertisement, &list))
			|| list == IntPtr.Zero)
		{
			return;
		}

		try
		{
			uint count;

			if (Failed(((delegate* unmanaged<IntPtr, uint*, int>)Slot(list, _slotSize))(list, &count)))
			{
				return;
			}

			for (uint index = 0; index < count; index++)
			{
				IntPtr item;

				if (Failed(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(list, _slotGetAt))(list, index, &item))
					|| item == IntPtr.Zero)
				{
					continue;
				}

				try
				{
					Block(item, signal);
				}
				finally
				{
					Release(item);
				}
			}
		}
		finally
		{
			Release(list);
		}
	}

	private static void Block(IntPtr item, short signal)
	{
		var data = Cast(item, _maker);

		if (data == IntPtr.Zero)
		{
			return;
		}

		try
		{
			ushort company;
			IntPtr buffer;

			if (Failed(((delegate* unmanaged<IntPtr, ushort*, int>)Slot(data, _slotCompany))(data, &company))
				|| Failed(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(data, _slotData))(data, &buffer))
				|| buffer == IntPtr.Zero)
			{
				return;
			}

			try
			{
				if (Read(buffer) is { } block)
				{
					Note(company, block, signal);
				}
			}
			finally
			{
				Release(buffer);
			}
		}
		finally
		{
			Release(data);
		}
	}

	private static byte[]? Read(IntPtr buffer)
	{
		uint length;

		if (Failed(((delegate* unmanaged<IntPtr, uint*, int>)Slot(buffer, _slotLength))(buffer, &length)) || length == 0)
		{
			return null;
		}

		// До самих байтов буфер WinRT пускает только через отдельный интерфейс: он не
		// описан в метаданных, потому что появился ещё до них.
		var bytes = Cast(buffer, _access);

		if (bytes == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			byte* start;

			if (Failed(((delegate* unmanaged<IntPtr, byte**, int>)Slot(bytes, _slotBytes))(bytes, &start)) || start is null)
			{
				return null;
			}

			// Копия, а не указатель: буфер живёт ровно до конца обратного вызова.
			return new ReadOnlySpan<byte>(start, (int)length).ToArray();
		}
		finally
		{
			Release(bytes);
		}
	}

	private static void Note(ushort company, byte[] block, short signal)
	{
		foreach (var rule in Store.Current.Beacons)
		{
			if (rule.Maker != company || rule.Level(block) is not { } percent)
			{
				continue;
			}

			var key = rule.Key(block);

			lock (_guard)
			{
				_heard[key] = (percent, DateTime.UtcNow);

				// В один сеанс одно устройство слышно десятки раз; держим самое близкое,
				// потому что привязку решает именно оно.
				if (!_sweep.TryGetValue(key, out var seen) || seen.Signal < signal)
				{
					_sweep[key] = new Sighting(key, rule.Name, percent, signal);
				}
			}
		}
	}

	private static IntPtr Callback()
	{
		// Обратный вызов — обычный объект COM: указатель на таблицу из четырёх методов.
		// Он один на всю жизнь программы, поэтому память под него не освобождается.
		var table = (IntPtr*)NativeMemory.Alloc((nuint)(sizeof(IntPtr) * 4));

		table[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&OnAsk;
		table[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&OnKeep;
		table[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&OnDrop;
		table[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHeard;

		var self = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
		*self = (IntPtr)table;

		return (IntPtr)self;
	}

	[UnmanagedCallersOnly]
	private static int OnAsk(IntPtr self, Guid* iid, IntPtr* result)
	{
		// Согласие на IAgileObject избавляет от посредника между квартирами COM: без него
		// WinRT полез бы маршалить обратный вызов и на этом спотыкался.
		if (*iid == _handler || *iid == _unknown || *iid == _agile)
		{
			*result = self;

			return 0;
		}

		*result = IntPtr.Zero;

		return _noInterface;
	}

	// Объект статический и живёт до конца программы: считать ссылки не для кого.
	[UnmanagedCallersOnly]
	private static uint OnKeep(IntPtr self) => 2;

	[UnmanagedCallersOnly]
	private static uint OnDrop(IntPtr self) => 1;

	[UnmanagedCallersOnly]
	private static int OnHeard(IntPtr self, IntPtr sender, IntPtr args)
	{
		try
		{
			Take(args);
		}
		catch (Exception exception)
		{
			// Из обратного вызова COM исключение выпускать нельзя: оно перейдёт границу
			// неуправляемого кода и уронит процесс целиком.
			Journal.Add("langLogBeaconFailed", exception.Message);
		}

		return 0;
	}

	private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

	private static bool Failed(int result) => result < 0;

	private static IntPtr Cast(IntPtr instance, Guid iid)
	{
		IntPtr result;

		return Failed(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &iid, &result))
			? IntPtr.Zero
			: result;
	}

	private static void Release(IntPtr instance)
	{
		if (instance != IntPtr.Zero)
		{
			((delegate* unmanaged<IntPtr, uint>)Slot(instance, 2))(instance);
		}
	}

	[LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int WindowsCreateString(string source, uint length, out IntPtr result);

	[LibraryImport("combase.dll")]
	private static partial int WindowsDeleteString(IntPtr value);

	[LibraryImport("combase.dll")]
	private static partial int RoGetActivationFactory(IntPtr className, in Guid iid, out IntPtr factory);
}
