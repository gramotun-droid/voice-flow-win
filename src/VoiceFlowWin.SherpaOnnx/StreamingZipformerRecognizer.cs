using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.SherpaEngine;

/// <summary>
/// Потоковое распознавание через sherpa-onnx, модель Zipformer-transducer.
/// </summary>
/// <remarks>
/// Распознаватель живёт в фоновом потоке и никогда не вызывается из UI:
/// декодирование блокирующее, и вызов из потока интерфейса подвешивал бы окно
/// на каждом кадре.
///
/// Zipformer отдаёт монотонно растущую гипотезу текущей фразы: в отличие от
/// прежнего движка, «финального» результата он сам не объявляет — конец фразы
/// определяет VAD приложения, а текст забирается вызовом
/// <see cref="FlushFinalResult"/>. Промежуточный результат публикуется не чаще
/// заданного интервала и только при изменении: лишние события создают дребезг
/// в overlay.
/// </remarks>
public sealed class StreamingZipformerRecognizer : IStreamingRecognizer
{
    private readonly ZipformerModelLoader _loader;
    private readonly ISettingsService _settings;
    private readonly ILogger<StreamingZipformerRecognizer> _logger;
    private readonly object _sync = new();

    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private string _lastPartial = string.Empty;
    private DateTimeOffset _lastPartialAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public StreamingZipformerRecognizer(
        ZipformerModelLoader loader,
        ISettingsService settings,
        ILogger<StreamingZipformerRecognizer>? logger = null)
    {
        _loader = loader;
        _settings = settings;
        _logger = logger ?? NullLogger<StreamingZipformerRecognizer>.Instance;
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _recognizer is not null;
            }
        }
    }

    public RecognitionLanguage CurrentLanguage { get; private set; } = RecognitionLanguage.Russian;

    public event EventHandler<PartialResultEventArgs>? PartialResult;

    public event EventHandler<PartialResultEventArgs>? FinalResult;

    public async Task PrepareAsync(RecognitionLanguage language, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Auto означает автоопределение на стороне Whisper; потоковая модель
        // всё равно работает на конкретном языке — берём русский как основной
        // язык интерфейса.
        var effective = language == RecognitionLanguage.Auto ? RecognitionLanguage.Russian : language;

        lock (_sync)
        {
            if (_recognizer is not null && CurrentLanguage == effective)
            {
                return;
            }
        }

        var streamingSettings = _settings.Current.Streaming;
        var modelPath = effective == RecognitionLanguage.Russian
            ? streamingSettings.RussianModelPath
            : streamingSettings.EnglishModelPath;

        var recognizer = await _loader
            .GetOrLoadAsync(effective, modelPath, streamingSettings.CpuThreads, cancellationToken)
            .ConfigureAwait(false);

        var stream = recognizer.CreateStream();

        lock (_sync)
        {
            _stream?.Dispose();
            _recognizer = recognizer;
            _stream = stream;
            CurrentLanguage = effective;
            _lastPartial = string.Empty;
        }

        _logger.LogInformation("Zipformer готов, язык {Language}.", effective);
    }

    public void ResetSegment()
    {
        lock (_sync)
        {
            if (_recognizer is null)
            {
                return;
            }

            // Состояние потока сбрасывается новым потоком: у sherpa-onnx это
            // дешевле и надёжнее, чем пытаться очистить старый.
            _stream?.Dispose();
            _stream = _recognizer.CreateStream();
            _lastPartial = string.Empty;
            _lastPartialAt = DateTimeOffset.MinValue;
        }
    }

    public void AcceptAudio(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        var samples = ToSamples(pcm);
        string? partial = null;

        lock (_sync)
        {
            if (_recognizer is null || _stream is null)
            {
                return;
            }

            try
            {
                _stream.AcceptWaveform(AudioFormat.SampleRate, samples);
                while (_recognizer.IsReady(_stream))
                {
                    _recognizer.Decode(_stream);
                }

                if (ShouldPollPartial())
                {
                    partial = _recognizer.GetResult(_stream).Text;
                    _lastPartialAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Падение native-библиотеки не должно уронить диктовку целиком.
                _logger.LogError(ex, "Ошибка sherpa-onnx при обработке аудио.");
                return;
            }
        }

        if (partial is null)
        {
            return;
        }

        partial = partial.Trim();
        if (partial.Length == 0 || string.Equals(partial, _lastPartial, StringComparison.Ordinal))
        {
            return;
        }

        _lastPartial = partial;
        PartialResult?.Invoke(this, new PartialResultEventArgs(partial));
    }

    public string FlushFinalResult()
    {
        lock (_sync)
        {
            if (_recognizer is null || _stream is null)
            {
                return string.Empty;
            }

            try
            {
                // Хвост аудио уже принят, но декодировать его нужно до конца:
                // иначе последние слова фразы останутся необработанными.
                _stream.InputFinished();
                while (_recognizer.IsReady(_stream))
                {
                    _recognizer.Decode(_stream);
                }

                var text = _recognizer.GetResult(_stream).Text.Trim();

                _stream.Dispose();
                _stream = _recognizer.CreateStream();
                _lastPartial = string.Empty;
                _lastPartialAt = DateTimeOffset.MinValue;

                return text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка sherpa-onnx при завершении фразы.");
                return string.Empty;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_sync)
        {
            _stream?.Dispose();
            _stream = null;

            // Сами модели держит загрузчик: он живёт дольше распознавателя.
            _recognizer = null;
        }
    }

    private bool ShouldPollPartial()
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.Current.Streaming.PartialResultIntervalMs));
        return DateTimeOffset.UtcNow - _lastPartialAt >= interval;
    }

    /// <summary>PCM 16 bit → нормализованные float, как того ждёт sherpa-onnx.</summary>
    internal static float[] ToSamples(ReadOnlySpan<byte> pcm)
    {
        var count = pcm.Length / AudioFormat.BytesPerSample;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm[(i * AudioFormat.BytesPerSample)..]) / 32768f;
        }

        return samples;
    }
}
