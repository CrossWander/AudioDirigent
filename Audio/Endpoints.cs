using System.Collections.Generic;

namespace AudioDirigent;

/// <summary>
/// Единственная дверь из окна в звук. Сама она почти ничего не делает — всё уходит ниже,
/// в Core Audio, — и в этом её смысл: пока окно звало Interop напрямую, любая правка того,
/// КАК меняется звук, означала поиск по разметке, кто ещё это делает. Теперь такое место
/// одно: когда устройство придётся переключать не через IPolicyConfig, менять надо здесь.
/// </summary>
internal static class Endpoints
{
	/// <summary>Устройства направления — с отключёнными и отсутствующими, их тоже показывают.</summary>
	public static List<AudioEndpoint> All(EDataFlow flow) => Audio.ListDevices(flow);

	/// <summary>То, на чём звук играет сейчас.</summary>
	public static AudioEndpoint? Current(EDataFlow flow) => Audio.GetDefault(flow, ERole.Multimedia);

	/// <summary>Сделать устройство тем, на чём звук играет. Здесь и живёт способ переключения.</summary>
	public static void MakeCurrent(AudioEndpoint device) => Audio.SetDefault(device.Id);

	/// <summary>Уровень устройства в процентах; null — устройство его не отдаёт.</summary>
	public static int? Level(AudioEndpoint device) => Audio.GetVolume(device.Id);

	public static bool SetLevel(AudioEndpoint device, int percent) => Audio.SetVolume(device.Id, percent);

	/// <summary>Что устройство даёт крутить сверх уровня: усиление, автоподстройка.</summary>
	public static List<MicrophoneKnob> Knobs(AudioEndpoint device) => Microphone.Knobs(device.Id);

	public static bool Turn(MicrophoneKnob knob, float decibels) => Microphone.SetLevel(knob, decibels);

	public static bool Switch(MicrophoneKnob knob, bool on) => Microphone.SetAutoGain(knob, on);

	/// <summary>Измеритель сигнала. Держит поток с устройства — отпускать обязательно.</summary>
	public static Meter? Signal(AudioEndpoint device) => Meter.Open(device.Id);
}
