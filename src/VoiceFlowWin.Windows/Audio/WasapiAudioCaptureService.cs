using System.Runtime.InteropServices;
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
                _device = ResolveDevice(deviceId);
                _logger.LogInformation(
                    "Микрофон выбран: {Device} ({Id}), состояние {State}",
                    _device.FriendlyName,
                    _device.ID,
                    _device.State);

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
                CaptureFailed?.Invoke(this, DescribeFailure(ex));
            }
        }
    }


    /// <summary>
    /// Выбирает микрофон, с которого действительно можно писать.
    /// </summary>
    /// <remarks>
    /// Устройство по умолчанию для роли Communications в Windows может быть не
    /// задано или указывать на отключённый endpoint — активация такого
    /// заканчивается ошибкой AUDCLNT_E_DEVICE_INVALIDATED. Поэтому выбор идёт
    /// по цепочке: заданное в настройках, затем умолчания для двух ролей, затем
    /// первый активный микрофон. Неактивные устройства отбрасываются сразу.
    /// </remarks>
    private MMDevice ResolveDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var configured = TryGetDevice(() => enumerator.GetDevice(deviceId));
            if (configured is not null)
            {
                return configured;
            }

            _logger.LogWarning("Микрофон из настроек недоступен, берётся устройство по умолчанию.");
        }

        var communications = TryGetDevice(() => enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
        if (communications is not null)
        {
            return communications;
        }

        var console = TryGetDevice(() => enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console));
        if (console is not null)
        {
            return console;
        }

        var active = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault();
        if (active is not null)
        {
            return active;
        }

        throw new InvalidOperationException("В системе нет доступного микрофона.");
    }

    private MMDevice? TryGetDevice(Func<MMDevice> factory)
    {
        try
        {
            var device = factory();
            if (device.State == DeviceState.Active)
            {
                return device;
            }

            _logger.LogWarning("Микрофон {Device} в состоянии {State} — пропущен.", device.FriendlyName, device.State);
            device.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Устройство записи недоступно.");
        }

        return null;
    }

    /// <summary>Переводит код WASAPI в то, что пользователь может исправить.</summary>
    private static string DescribeFailure(Exception exception) => exception switch
    {
        COMException { ErrorCode: unchecked((int)0x88890004) } =>
            "Микрофон недоступен: устройство отключено, выключено в системе или занято. " +
            "Проверьте микрофон в параметрах звука Windows и выберите его в настройках приложения.",
        COMException { ErrorCode: unchecked((int)0x8889000A) } =>
            "Микрофон занят другим приложением. Закройте программу, которая его использует, и повторите.",
        COMException { ErrorCode: unchecked((int)0x80070005) } =>
            "Windows запретила доступ к микрофону. Разрешите его в «Параметры → Конфиденциальность → Микрофон».",
        _ => "Не удалось начать запись с микрофона: " + exception.Message,
    };

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
