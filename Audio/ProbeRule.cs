using System.Collections.Generic;

namespace AudioDirigent;

/// <summary>
/// Описание приёмника беспроводной гарнитуры: какое HID-устройство спрашивать, что ему
/// слать и где в ответе искать состояние. Числа пишутся шестнадцатеричными — так их
/// печатают анализаторы протокола, из которых эти значения и берутся. Готовый пример
/// протокола есть в README; программа своих не знает.
/// </summary>
/// <param name="Vendors">Коды производителей, например «03F0»; пустой список — любой.</param>
/// <param name="UsagePage">Usage page управляющей коллекции, например «01C0».</param>
/// <param name="Request">Байты запроса: «0C 02 03 01 00 02».</param>
/// <param name="StatusByte">Номер байта ответа, в котором лежит состояние.</param>
/// <param name="OnValue">Значение этого байта, означающее «гарнитура включена».</param>
/// <param name="Covers">Часть имени устройств гарнитуры: их скрывают, пока она выключена.</param>
internal sealed record ProbeRule(
	List<string> Vendors,
	string UsagePage,
	string Request,
	int StatusByte,
	string OnValue,
	string Covers);
