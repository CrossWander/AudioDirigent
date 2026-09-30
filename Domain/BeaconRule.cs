using System;
using System.Globalization;

namespace AudioDirigent;

/// <summary>Где в блоке лежит заряд: в полубайте или в целом байте.</summary>
internal enum BeaconPart
{
	Low,
	High,
	Whole,
}

/// <summary>
/// Описание маяка: как из объявления Bluetooth достать заряд. Часть устройств не сообщает
/// его ни Windows, ни по проводу — только кричит в эфир всем подряд, и прочесть это может
/// кто угодно, включая нас.
///
/// Правило — данные, а не код: в программе нет ни одного производителя, есть номер, по
/// которому его узнают, и смещения. Один пример уже записан в config.json; остальные
/// дописываются туда же, без пересборки.
/// </summary>
/// <param name="Name">Имя для файла: программа его не читает, читает человек.</param>
/// <param name="Company">Код производителя из реестра Bluetooth, например «004C».</param>
/// <param name="Prefix">С каких байтов начинается нужный блок, например «0719»; пусто — с любых.</param>
/// <param name="ModelAt">С какого байта идёт код модели; -1 — модели в блоке нет.</param>
/// <param name="ModelLength">Сколько байтов занимает код модели.</param>
/// <param name="BatteryAt">Байт, в котором лежит заряд.</param>
/// <param name="Part">Какая его половина: младшая, старшая или весь байт.</param>
/// <param name="Step">Во что превращается единица: 10 — значение хранится десятками процентов.</param>
/// <param name="Highest">Наибольшее осмысленное значение; выше — «устройство не знает».</param>
internal sealed record BeaconRule(
	string Name,
	string Company,
	string Prefix,
	int ModelAt,
	int ModelLength,
	int BatteryAt,
	BeaconPart Part,
	int Step,
	int Highest)
{
	/// <summary>
	/// Готовое правило, с которым программа приезжает. Не знание о производителе, а
	/// значение по умолчанию — ровно как «Ctrl+Alt+P» в настройке горячей клавиши: лежит
	/// в config.json, правится там же и оттуда же удаляется.
	/// </summary>
	public static BeaconRule Seed => new("Apple", "004C", "0719", 3, 2, 6, BeaconPart.Low, 10, 10);

	/// <summary>Код производителя числом; null — в файле не шестнадцатеричное число.</summary>
	public ushort? Maker =>
		ushort.TryParse(Company, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;

	/// <summary>Блок этого маяка: начинается с нужных байтов и достаёт до заряда.</summary>
	public bool Fits(ReadOnlySpan<byte> block)
	{
		if (BatteryAt < 0 || BatteryAt >= block.Length)
		{
			return false;
		}

		var prefix = Bytes(Prefix);

		return prefix.Length <= block.Length && block[..prefix.Length].SequenceEqual(prefix);
	}

	/// <summary>Заряд в процентах; null — устройство честно сказало, что не знает.</summary>
	public int? Level(ReadOnlySpan<byte> block)
	{
		if (!Fits(block))
		{
			return null;
		}

		var raw = Part switch
		{
			BeaconPart.Low => block[BatteryAt] & 0x0F,
			BeaconPart.High => block[BatteryAt] >> 4,
			_ => block[BatteryAt],
		};

		// «Не знаю» — это не ноль: у Apple в полубайт пишут 15, и показать 150 % было бы
		// смешно, а 0 % — страшно. Обе беды лечит один порог.
		return raw > Highest ? null : Math.Min(100, raw * Step);
	}

	/// <summary>
	/// Чем устройство опознаётся в следующий раз. Адрес для этого не годится: он у многих
	/// меняется по таймеру именно затем, чтобы по нему не следили. Остаётся код модели.
	/// </summary>
	public string Key(ReadOnlySpan<byte> block)
	{
		if (ModelAt < 0 || ModelLength <= 0 || ModelAt + ModelLength > block.Length)
		{
			return Company;
		}

		return Company + "-" + Convert.ToHexString(block.Slice(ModelAt, ModelLength));
	}

	private static byte[] Bytes(string hex)
	{
		try
		{
			return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
		}
		catch (FormatException)
		{
			// Файл правит человек, и «07 19» с пробелом здесь разумнее считать опиской,
			// чем поводом уронить разбор всех остальных правил.
			return [];
		}
	}
}

/// <summary>Услышанный маяк: что нашлось в эфире за один сеанс прослушивания.</summary>
/// <param name="Signal">Сила сигнала в дБм — по ней выбирают ближайшее устройство при привязке.</param>
internal sealed record Sighting(string Key, string Name, int Percent, int Signal);
