using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceFlowWin.Audio;
using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.Windows.Audio;

/// <summary>
/// Захват микрофона через WASAPI.
/// </summary>
/// <remarks>
/// Устройство отдаёт звук в своём формате (обычно 32-битный float, 44.1 или
/// 48 kHz, стерео), а конвейеру нужен PCM 16 kHz mono 16 bit — преобразование
/// выполняется сразу в обработчике, чтобы дальше по цепочке формат был один.
///
/// Отключение микрофона на ходу не должно ронять приложение: NAudio сообщает
/// об этом через RecordingStopped с исключением, и мы превращаем его в событие
/// <see cref="CaptureFailed"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<WasapiAudioCaptureService> _logger;
    private readonly object _sync = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private double _gainDb;
    private bool _disposed;

    public WasapiAudioCaptureService(ILogger<WasapiAudioCaptureService>? logger = null) =>
        _logger = logger ?? NullLogger<WasapiAudioCaptureService>.Instance;

    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    public event EventHandler<AudioFrameEventArgs>? FrameCaptured;

    public event EventHandler<string>? CaptureFailed;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = string.Empty;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = defaultDevice.ID;
            }
            catch (Exception)
            {
                // Микрофона по умолчанию может не быть вовсе.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось перечислить устройства записи.");
        }

        return result;
    }

    public void Start(string? deviceId, double gainDb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_capture is not null)
            {
                return;
            }

            _gainDb = gainDb;

            try
            {
                var enumerator = new MMDeviceEnumerator();
                _device = string.IsNullOrWhiteSpace(deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : enumerator.GetDevice(deviceId);

                _capture = new WasapiCapture(_device)
                {
                    // Короткий буфер — меньше задержка до первой гипотезы.
                    ShareMode = AudioClientShareMode.Shared,
                };

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                _logger.LogInformation("Захват микрофона начат: {Device}", _device.FriendlyName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось начать захват микрофона.");
                CleanupUnsafe();
                CaptureFailed?.Invoke(this, "Не удалось начать запись с микрофона: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_capture is null)
            {
                return;
            }

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при остановке захвата.");
            }

            CleanupUnsafe();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var format = _capture?.WaveFormat;
        if (format is null)
        {
            return;
        }

        try
        {
            var source = e.Buffer.AsSpan(0, e.BytesRecorded);
            var pcm = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? AudioFormatConverter.FromFloat32(source, format.SampleRate, format.Channels, _gainDb)
                : AudioFormatConverter.FromPcm16(source, format.SampleRate, format.Channels, _gainDb);

            if (pcm.Length == 0)
            {
                return;
            }

            FrameCaptured?.Invoke(this, new AudioFrameEventArgs(pcm, pcm.Length, AudioFormatConverter.ComputeLevelDb(pcm)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки аудиокадра.");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _logger.LogError(e.Exception, "Запись прервана.");
        CaptureFailed?.Invoke(this, "Запись прервана: " + e.Exception.Message);

        lock (_sync)
        {
            CleanupUnsafe();
        }
    }

    private void CleanupUnsafe()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
    }
}
