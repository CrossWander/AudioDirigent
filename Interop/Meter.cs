using System;
using System.Runtime.InteropServices;

namespace AudioDirigent;

/// <summary>
/// Измеритель сигнала микрофона. Пока с микрофона никто не пишет, мерить нечего, поэтому
/// измеритель открывает свой поток — ровно за этим его открывает и панель звука Windows,
/// когда показывает бегущую полоску.
///
/// Пик считается по самим отсчётам, а не спрашивается у точки. Счётчик точки
/// (IAudioMeterInformation) стоит в топологии до узла усиления и на замерах занижал ровно
/// на его величину: у гарнитуры с усилением +30 dB речь на -15 dBFS он показывал как -55,
/// то есть как тишину. Чем сильнее поднимали усиление, тем сильнее он врал.
/// </summary>
internal sealed class Meter : IDisposable
{
	private const int _shared = 0;
	// AUDCLNT_BUFFERFLAGS_SILENT: буфер отдали незаполненным, в нём мусор, а не тишина.
	private const uint _silent = 0x2;
	// Буфера на пятую долю секунды хватает: его успевают вычерпать между показами полоски.
	private const long _buffer = 2_000_000;

	private readonly IAudioClient _client;
	private readonly IAudioCaptureClient _capture;
	private readonly int _channels;

	private float _loudest;

	private Meter(IAudioClient client, IAudioCaptureClient capture, int channels)
	{
		_client = client;
		_capture = capture;
		_channels = channels;
	}

	/// <summary>Открыть поток и начать мерить; null — устройство не дало.</summary>
	public static Meter? Open(string endpointId)
	{
		IntPtr format = IntPtr.Zero;
		try
		{
			if (Audio.Activate<IAudioClient>(endpointId, Audio.ClsCtxAll) is not { } client
				|| client.GetMixFormat(out format) != 0)
			{
				return null;
			}

			// Смесь общего режима Windows отдаёт 32-битной с плавающей точкой всегда. Если
			// вдруг нет — мерить нечем, и лучше не показать полоску, чем показать ложь.
			if (Marshal.ReadInt16(format, 14) != 32)
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

			return new Meter(client, capture, Marshal.ReadInt16(format, 2));
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

	/// <summary>Самый громкий отсчёт с прошлого опроса, 0…1.</summary>
	public float Peak
	{
		get
		{
			try
			{
				Drain();
			}
			catch (COMException)
			{
				return 0;
			}

			var peak = _loudest;
			_loudest = 0;

			return peak;
		}
	}

	// Вычерпывать надо в любом случае: не вычерпав, мы переполним буфер и поток встанет.
	// Заодно и меряем — отсчёты уже в руках, и это честнее, чем спрашивать число у точки.
	private void Drain()
	{
		while (_capture.GetNextPacketSize(out var frames) == 0 && frames > 0)
		{
			if (_capture.GetBuffer(out var data, out var taken, out var flags, out _, out _) != 0)
			{
				return;
			}

			// Флаг тишины означает, что буфер не заполняли вовсе: читать его нельзя.
			if ((flags & _silent) == 0 && data != IntPtr.Zero)
			{
				Look(data, (int)taken * _channels);
			}

			_capture.ReleaseBuffer(taken);
		}
	}

	private unsafe void Look(IntPtr data, int count)
	{
		var sample = (float*)data;
		for (var i = 0; i < count; i++)
		{
			var value = Math.Abs(sample[i]);
			if (value > _loudest)
			{
				_loudest = value;
			}
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
