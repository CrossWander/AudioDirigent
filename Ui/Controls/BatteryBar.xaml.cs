using System;
using System.Windows;
using System.Windows.Controls;

namespace AudioDirigent;

/// <summary>
/// Заряд батарейкой. Показывается в двух местах — на карточке подключения и в шапке окна, —
/// и элемент общий именно поэтому: нарисованные порознь, они разошлись бы при первой же
/// правке, и одно число выглядело бы двумя разными вещами в одной программе.
/// </summary>
public partial class BatteryBar : UserControl
{
	// Ширина корпуса за вычетом его полей: полоса рисуется долей от неё.
	private const double _inside = 22 - 5;

	public BatteryBar() => InitializeComponent();

	/// <summary>Показать заряд; null — устройство его не сообщает, и батарейки просто нет.</summary>
	public void Show(int? charge)
	{
		if (charge is not { } level)
		{
			Visibility = Visibility.Collapsed;

			return;
		}

		Visibility = Visibility.Visible;
		Percent.Text = $"{level}%";
		Fill.Width = Math.Max(1, _inside * Math.Clamp(level, 0, 100) / 100.0);
	}
}
