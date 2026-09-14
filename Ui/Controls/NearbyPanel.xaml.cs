using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AudioDirigent;

/// <summary>Строка списка Bluetooth — ровно то, что показывает шаблон.</summary>
/// <param name="Endpoint">Точка вывода этого устройства; null — звука у него в системе нет.</param>
internal sealed record NearbyRow(
	BluetoothDevice Device,
	string Name,
	string Note,
	string Link,
	bool Connected,
	bool Paired,
	bool Hidden,
	bool Linkable,
	AudioEndpoint? Endpoint);

/// <summary>
/// Устройства Bluetooth: что помнит радиомодуль, что из этого звучит и что можно поднять
/// отсюда, не уходя в настройки Windows.
/// </summary>
public partial class NearbyPanel : UserControl
{
	private Switcher? _switcher;
	private bool _loading;
	private bool _busy;

	public NearbyPanel() => InitializeComponent();

	internal void Attach(Switcher switcher) => _switcher = switcher;

	/// <summary>Перечитать список. Эфир при этом не трогаем — только то, что система уже знает.</summary>
	internal void Reload()
	{
		_loading = true;
		ShowHiddenToggle.IsChecked = Store.Current.ShowHiddenBluetooth;
		_loading = false;

		if (!Bluetooth.Present)
		{
			NoRadio.Visibility = Visibility.Visible;
			Empty.Visibility = Visibility.Collapsed;
			Rows.ItemsSource = null;
			ScanButton.IsEnabled = false;

			return;
		}

		NoRadio.Visibility = Visibility.Collapsed;
		Fill(Bluetooth.Devices());
	}

	private void Fill(List<BluetoothDevice> devices)
	{
		// Звук привязан к устройству адресом: он стоит прямо в пути узла PnP той точки,
		// через которую этот наушник играет.
		var endpoints = Audio.ListDevices(EDataFlow.Render)
			.Where(device => device.Bluetooth && device.Node is not null)
			.ToList();

		var hidden = Store.Current.HiddenBluetooth.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var showHidden = Store.Current.ShowHiddenBluetooth;

		var rows = devices
			.Where(device => showHidden || !hidden.Contains(device.Mac))
			.OrderByDescending(device => device.Connected)
			.ThenByDescending(device => device.Audio)
			.ThenBy(device => device.Name)
			.Select(device => Row(device, endpoints, hidden.Contains(device.Mac)))
			.ToList();

		Rows.ItemsSource = rows;
		Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private static NearbyRow Row(BluetoothDevice device, List<AudioEndpoint> endpoints, bool hidden)
	{
		var endpoint = endpoints.FirstOrDefault(
			point => point.Node!.Contains(device.Mac, StringComparison.OrdinalIgnoreCase));

		var state = Localization.Get(device.Connected ? "langBluetoothConnected"
			: device.Paired ? "langBluetoothPaired" : "langBluetoothNew");

		return new NearbyRow(
			Device: device,
			Name: string.IsNullOrEmpty(device.Name) ? device.Pretty : device.Name,
			Note: $"{Localization.Get(device.Kind)}  ·  {state}  ·  {device.Pretty}",
			Link: Localization.Get(device.Connected ? "langDisconnect" : "langConnect"),
			Connected: device.Connected,
			Paired: device.Paired,
			Hidden: hidden,
			// Поднять связь можно только тому, у кого в системе уже есть точка вывода:
			// команда идёт к ней, а не к самому радиомодулю.
			Linkable: endpoint is not null,
			Endpoint: endpoint);
	}

	private static NearbyRow? Of(object sender) => (sender as FrameworkElement)?.DataContext as NearbyRow;

	// Связь поднимается и рвётся через ту же точку вывода, что и в списке устройств: команда
	// уходит мгновенно, а связь встаёт ещё секунду-другую.
	private async void OnLink(object sender, RoutedEventArgs e)
	{
		if (Of(sender) is not { Endpoint: { } endpoint } row || _busy)
		{
			return;
		}

		_busy = true;
		var connect = !row.Connected;
		var done = await Task.Run(() => connect ? BluetoothAudio.Connect(endpoint) : BluetoothAudio.Disconnect(endpoint));
		_busy = false;

		if (!done)
		{
			_switcher?.Log(connect ? "langLogConnectFailed" : "langLogDisconnectFailed", row.Name);
		}

		Reload();
	}

	// Знакомство ведёт Windows: у неё уже есть и сверка кода, и ввод пин-кода, и перевод
	// на язык человека. Своя пара окон вышла бы беднее, а ошибиться в ней проще.
	private async void OnPair(object sender, RoutedEventArgs e)
	{
		if (Of(sender) is not { } row || _busy)
		{
			return;
		}

		_busy = true;
		var owner = new WindowInteropHelper(Window.GetWindow(this)!).Handle;
		var code = await Task.Run(() => Bluetooth.Pair(row.Device, owner));
		_busy = false;

		_switcher?.Log(code == 0 ? "langLogPaired" : "langLogPairFailed", row.Name);
		Reload();
	}

	private void OnHide(object sender, RoutedEventArgs e)
	{
		if (_loading || Of(sender) is not { } row || sender is not CheckBox box)
		{
			return;
		}

		var hidden = Store.Current.HiddenBluetooth;
		if (box.IsChecked == true)
		{
			if (!hidden.Contains(row.Device.Mac, StringComparer.OrdinalIgnoreCase))
			{
				hidden.Add(row.Device.Mac);
			}
		}
		else
		{
			hidden.RemoveAll(mac => mac.Equals(row.Device.Mac, StringComparison.OrdinalIgnoreCase));
		}

		Store.Save();
		Reload();
	}

	private void OnShowHidden(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}

		Store.Current.ShowHiddenBluetooth = ShowHiddenToggle.IsChecked == true;
		Store.Save();
		Reload();
	}

	// Незнакомое устройство находится только опросом эфира, а опрос занимает тот же
	// радиомодуль, через который играет музыка: на эти секунды звук захлёбывается. Молча
	// такое делать нельзя — человек решит, что сломалась программа.
	private async void OnScan(object sender, RoutedEventArgs e)
	{
		if (_busy || MessageBox.Show(Window.GetWindow(this)!,
				Localization.Get("langBluetoothScanWarning"),
				Localization.Get("langBluetoothScan"),
				MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
		{
			return;
		}

		_busy = true;
		ScanButton.IsEnabled = false;
		ScanNote.Text = Localization.Get("langBluetoothScanning");

		var devices = await Task.Run(() => Bluetooth.Devices(inquiry: true));

		ScanNote.Text = Localization.Get("langBluetoothScanHint");
		ScanButton.IsEnabled = true;
		_busy = false;

		Fill(devices);
	}
}
