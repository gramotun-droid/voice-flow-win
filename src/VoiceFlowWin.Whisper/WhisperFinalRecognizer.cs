using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Whisper.net;
using Whisper.net.Logger;

namespace VoiceFlowWin.WhisperEngine;

/// <summary>
/// Финальное распознавание сегмента через whisper.cpp.
/// </summary>
/// <remarks>
/// Модель загружается один раз в <see cref="WhisperFactory"/> и живёт до
/// выхода из приложения: запуск отдельного процесса на каждую фразу стоил бы
/// секунды на одну только загрузку весов. Процессор пересоздаётся лишь когда
/// меняются язык, число потоков или текстовый контекст, потому что эти
/// параметры задаются при сборке.
/// </remarks>
public sealed class WhisperFinalRecognizer : IFinalRecognizer
{
    private readonly ISettingsService _settings;
    private readonly ILogger<WhisperFinalRecognizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string _processorKey = string.Empty;
    private string _loadedModelPath = string.Empty;
    private bool _disposed;

    public WhisperFinalRecognizer(ISettingsService settings, ILogger<WhisperFinalRecognizer>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<WhisperFinalRecognizer>.Instance;
    }

    public bool IsReady => _factory is not null;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var modelPath = _settings.Current.Whisper.ModelPath;
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new InvalidOperationException(
                "Модель проверки фраз (Whisper) не установлена. " +
                "Откройте настройки, вкладка «Модели», и скачайте её.");
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Модель Whisper не найдена.", modelPath);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeProcessorUnsafe();
            _factory?.Dispose();

            // Загрузка весов — это секунды и гигабайты; уводим её с UI-потока.
            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), cancellationToken).ConfigureAwait(false);
            _loadedModelPath = modelPath;
            _logger.LogInformation("Модель Whisper загружена: {Path}", modelPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FinalRecognitionResult> TranscribeAsync(FinalRecognitionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stopwatch = Stopwatch.StartNew();

        if (request.Pcm.Length == 0)
        {
            return new FinalRecognitionResult(request.SegmentId, string.Empty, stopwatch.Elapsed, true);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is null)
            {
                return FinalRecognitionResult.Failure(request.SegmentId, "Модель Whisper не загружена.", stopwatch.Elapsed);
            }

            var processor = GetOrCreateProcessorUnsafe(request);
            var samples = ConvertToFloatSamples(request.Pcm);

            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            return new FinalRecognitionResult(request.SegmentId, text.ToString().Trim(), stopwatch.Elapsed, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper не смог обработать сегмент {SegmentId}.", request.SegmentId);
            return FinalRecognitionResult.Failure(request.SegmentId, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeProcessorUnsafe();
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }

    private WhisperProcessor GetOrCreateProcessorUnsafe(FinalRecognitionRequest request)
    {
        var whisperSettings = _settings.Current.Whisper;
        var language = request.Language.ToCode();
        var prompt = whisperSettings.UsePreviousTextContext ? request.PreviousContext : null;
        var key = $"{language}|{whisperSettings.CpuThreads}|{prompt}";

        if (_processor is not null && string.Equals(_processorKey, key, StringComparison.Ordinal))
        {
            return _processor;
        }

        DisposeProcessorUnsafe();

        var builder = _factory!.CreateBuilder()
            .WithThreads(Math.Max(1, whisperSettings.CpuThreads))
            // Перевод выключен: результат должен остаться на языке речи.
            .WithNoContext();

        builder = language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            // Контекст помогает с формами слов и терминами, но передаём только
            // последние слова — иначе Whisper начинает повторять старый текст.
            builder = builder.WithPrompt(prompt);
        }

        _processor = builder.Build();
        _processorKey = key;
        return _processor;
    }

    private void DisposeProcessorUnsafe()
    {
        _processor?.Dispose();
        _processor = null;
        _processorKey = string.Empty;
    }

    /// <summary>PCM 16 bit → нормализованные float, как того ждёт whisper.cpp.</summary>
    internal static float[] ConvertToFloatSamples(byte[] pcm)
    {
        var sampleCount = pcm.Length / AudioFormat.BytesPerSample;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm, i * AudioFormat.BytesPerSample) / 32768f;
        }

        return samples;
    }
}
