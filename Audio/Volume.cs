using System;
using System.Linq;

namespace AudioDirigent;

/// <summary>
/// Громкость, на которую программа ставит устройство, когда сама его выбрала. Windows
/// помнит громкость каждого устройства и без нас, так что правила нужны только тем, кому
/// нужно держать устройство на постоянном уровне. Пустой список ничего не меняет.
/// </summary>
internal static class Volume
{
	/// <summary>Поставить устройству уровень, если для него есть правило.</summary>
	public static void Apply(AudioEndpoint device, Action<string, object?[]> log)
	{
		if (For(device) is { } match && Audio.SetVolume(device.Id, match.Percent))
		{
			log("langLogVolume", [device.Name, match.Percent]);
		}
	}

	/// <summary>Правило, по которому устройство получает уровень; null — уровень не закреплён.</summary>
	public static VolumeRule? For(AudioEndpoint device) =>
		// Первое подходящее правило: порядок в файле задаёт пользователь.
		Store.Current.Volume.FirstOrDefault(rule => Catches(rule, device));

	/// <summary>Закрепить уровень: правило, которое уже ловит устройство, правим, иначе заводим по имени.</summary>
	public static void Pin(AudioEndpoint device, int percent)
	{
		var rules = Store.Current.Volume;
		var index = rules.FindIndex(rule => Catches(rule, device));

		if (index < 0)
		{
			rules.Add(new VolumeRule(device.Name, percent));
		}
		else
		{
			rules[index] = rules[index] with { Percent = percent };
		}

		Store.Save();
	}

	/// <summary>Перестать закреплять уровень: снимаются все правила, которые ловят устройство.</summary>
	public static void Unpin(AudioEndpoint device)
	{
		Store.Current.Volume.RemoveAll(rule => Catches(rule, device));
		Store.Save();
	}

	private static bool Catches(VolumeRule rule, AudioEndpoint device) =>
		rule.Match.Length > 0 && Config.Matches(device, rule.Match);
}
