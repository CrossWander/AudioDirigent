using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioDirigent;

/// <summary>
/// Общее для всего, что живёт в списке устройств. Список держит две породы строк — сами
/// устройства и заголовки разделов, — и разметка выбирает шаблон по типу, а не по флагу.
/// Флаг нужен коду: выделение относится к устройству, а заголовок его не принимает.
/// </summary>
internal abstract class Notifier : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Это заголовок раздела, а не устройство.</summary>
	public virtual bool IsSection => false;

	protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
	{
		if (Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
	}
}
