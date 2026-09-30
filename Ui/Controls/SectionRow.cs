namespace AudioDirigent;

/// <summary>
/// Заголовок раздела в списке. Разделов два, и оба свёрнуты по умолчанию: в них лежит то,
/// о чём решение уже принято или принимать нечего — запрещённые и те, которых сейчас нет.
/// Прятать их насовсем нельзя, иначе непонятно, куда делось устройство; держать раскрытыми
/// тоже — из девятнадцати строк живых обычно три.
/// </summary>
internal sealed class SectionRow : Notifier
{
	private string _title = "";
	private int _count;
	private bool _open;

	public override bool IsSection => true;

	public string Title { get => _title; set => Set(ref _title, value); }

	/// <summary>Сколько устройств внутри: без этого свёрнутый раздел выглядит пустым.</summary>
	public int Count { get => _count; set => Set(ref _count, value); }

	public bool Open { get => _open; set => Set(ref _open, value); }
}
