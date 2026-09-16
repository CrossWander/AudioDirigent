using System;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Измеритель сигнала микрофона. Сам по себе пиковый уровень конечной точки всегда ноль:
/// Windows считает его по идущему через точку потоку, а пока с микрофона никто не пишет,
/// потока нет. Поэтому измеритель открывает свой — ровно за этим его открывает и панель
/// звука Windows, когда показывает бегущую полоску.
/// </summary>
internal sealed class Meter : IDisposable
{
	private const int _shared = 0;
	// Буфера на пятую долю секунды хватает: его успевают вычерпать между показами полоски.
	private const long _buffer = 2_000_000;

	private readonly IAudioClient _client;
	private readonly IAudioCaptureClient _capture;
	private readonly IAudioMeterInformation _meter;

	private Meter(IAudioClient client, IAudioCaptureClient capture, IAudioMeterInformation meter)
	{
		_client = client;
		_capture = capture;
		_meter = meter;
	}

	/// <summary>Открыть поток и начать мерить; null — устройство не дало.</summary>
	public static Meter? Open(string endpointId)
	{
		IntPtr format = IntPtr.Zero;
		try
		{
			if (Audio.Activate<IAudioClient>(endpointId, Audio.ClsCtxAll) is not { } client
				|| Audio.Activate<IAudioMeterInformation>(endpointId, Audio.ClsCtxAll) is not { } meter
				|| client.GetMixFormat(out format) != 0)
			{
				return null;
			}

			// Формат берём тот, в котором точка и так работает: пересчёта не нужно —
			// данные мы выбрасываем, важен сам факт идущего потока.
			if (client.Initialize(_shared, 0, _buffer, 0, format, IntPtr.Zero) != 0)
			{
				return null;
			}

			var iid = typeof(IAudioCaptureClient).GUID;
			if (client.GetService(ref iid, out var service) != 0 || service is not IAudioCaptureClient capture
				|| client.Start() != 0)
			{
				return null;
			}

			return new Meter(client, capture, meter);
		}
		catch (COMException)
		{
			// Устройство исчезло, пока мы к нему подключались.
			return null;
		}
		finally
		{
			// Формат отдан на наших руках — освобождаем его в любом случае.
			if (format != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(format);
			}
		}
	}

	/// <summary>Пик сигнала, 0…1.</summary>
	public float Peak
	{
		get
		{
			try
			{
				Drain();

				return _meter.GetPeakValue();
			}
			catch (COMException)
			{
				return 0;
			}
		}
	}

	// Записанное нам не нужно, но не вычерпав его, мы переполним буфер и поток встанет.
	private void Drain()
	{
		while (_capture.GetNextPacketSize(out var frames) == 0 && frames > 0)
		{
			if (_capture.GetBuffer(out _, out var taken, out _, out _, out _) != 0)
			{
				return;
			}

			_capture.ReleaseBuffer(taken);
		}
	}

	public void Dispose()
	{
		try
		{
			_client.Stop();
		}
		catch (COMException)
		{
			// Устройство уже исчезло — останавливать нечего.
		}

		Marshal.ReleaseComObject(_capture);
		Marshal.ReleaseComObject(_meter);
		Marshal.ReleaseComObject(_client);
	}

	[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioClient
	{
		[PreserveSig]
		int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);

		[PreserveSig]
		int GetBufferSize(out uint frames);

		[PreserveSig]
		int GetStreamLatency(out long latency);

		[PreserveSig]
		int GetCurrentPadding(out uint frames);

		[PreserveSig]
		int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);

		[PreserveSig]
		int GetMixFormat(out IntPtr format);

		[PreserveSig]
		int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

		[PreserveSig]
		int Start();

		[PreserveSig]
		int Stop();

		[PreserveSig]
		int Reset();

		[PreserveSig]
		int SetEventHandle(IntPtr handle);

		[PreserveSig]
		int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
	}

	[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioCaptureClient
	{
		[PreserveSig]
		int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long position, out long counter);

		[PreserveSig]
		int ReleaseBuffer(uint frames);

		[PreserveSig]
		int GetNextPacketSize(out uint frames);
	}
}
