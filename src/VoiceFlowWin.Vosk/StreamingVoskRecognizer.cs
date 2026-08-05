using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Vosk;

namespace VoiceFlowWin.VoskEngine;

/// <summary>
/// Потоковое распознавание через Vosk.
/// </summary>
/// <remarks>
/// Распознаватель живёт в фоновом потоке и никогда не вызывается из UI:
/// AcceptWaveform блокирующий, и вызов из потока интерфейса подвешивал бы
/// окно на каждом кадре. Промежуточный результат опрашивается не чаще, чем
/// задано в настройках — Vosk отдаёт его почти мгновенно, но лишние события
/// только создают дребезг в overlay.
/// </remarks>
public sealed class StreamingVoskRecognizer : IStreamingRecognizer
{
    private readonly VoskModelLoader _loader;
    private readonly ISettingsService _settings;
    private readonly ILogger<StreamingVoskRecognizer> _logger;
    private readonly object _sync = new();

    private VoskRecognizer? _recognizer;
    private string _lastPartial = string.Empty;
    private DateTimeOffset _lastPartialAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public StreamingVoskRecognizer(
        VoskModelLoader loader,
        ISettingsService settings,
        ILogger<StreamingVoskRecognizer>? logger = null)
    {
        _loader = loader;
        _settings = settings;
        _logger = logger ?? NullLogger<StreamingVoskRecognizer>.Instance;
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

        // Auto означает автоопределение на стороне Whisper; потоковый Vosk
        // всё равно должен работать на конкретном языке — берём русский как
        // основной язык интерфейса.
        var effective = language == RecognitionLanguage.Auto ? RecognitionLanguage.Russian : language;

        lock (_sync)
        {
            if (_recognizer is not null && CurrentLanguage == effective)
            {
                return;
            }
        }

        var voskSettings = _settings.Current.Vosk;
        var modelPath = effective == RecognitionLanguage.Russian
            ? voskSettings.RussianModelPath
            : voskSettings.EnglishModelPath;

        var model = await _loader.GetOrLoadAsync(effective, modelPath, cancellationToken).ConfigureAwait(false);
        var recognizer = new VoskRecognizer(model, AudioFormat.SampleRate);
        recognizer.SetMaxAlternatives(0);
        recognizer.SetWords(false);

        lock (_sync)
        {
            _recognizer?.Dispose();
            _recognizer = recognizer;
            CurrentLanguage = effective;
            _lastPartial = string.Empty;
        }

        _logger.LogInformation("Vosk готов, язык {Language}.", effective);
    }

    public void ResetSegment()
    {
        lock (_sync)
        {
            _recognizer?.Reset();
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

        // Vosk API принимает массив, а не Span, поэтому копия неизбежна.
        var buffer = pcm.ToArray();
        string? partialJson = null;
        string? finalJson = null;

        lock (_sync)
        {
            if (_recognizer is null)
            {
                return;
            }

            try
            {
                if (_recognizer.AcceptWaveform(buffer, buffer.Length))
                {
                    finalJson = _recognizer.Result();
                }
                else if (ShouldPollPartial())
                {
                    partialJson = _recognizer.PartialResult();
                    _lastPartialAt = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Падение native-библиотеки не должно уронить диктовку целиком.
                _logger.LogError(ex, "Ошибка Vosk при обработке аудио.");
                return;
            }
        }

        if (finalJson is not null)
        {
            var text = VoskResultParser.ParseFinal(finalJson);
            if (text.Length > 0)
            {
                FinalResult?.Invoke(this, new PartialResultEventArgs(text));
            }

            return;
        }

        if (partialJson is null)
        {
            return;
        }

        var partial = VoskResultParser.ParsePartial(partialJson);
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
            if (_recognizer is null)
            {
                return string.Empty;
            }

            try
            {
                var text = VoskResultParser.ParseFinal(_recognizer.FinalResult());
                _recognizer.Reset();
                _lastPartial = string.Empty;
                return text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка Vosk при получении финального результата.");
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
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private bool ShouldPollPartial()
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings.Current.Vosk.PartialResultIntervalMs));
        return DateTimeOffset.UtcNow - _lastPartialAt >= interval;
    }
}
