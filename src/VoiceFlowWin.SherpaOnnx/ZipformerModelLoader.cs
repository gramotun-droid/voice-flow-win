using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.SherpaEngine;

/// <summary>Файлы одной модели Zipformer внутри её каталога.</summary>
public sealed record ZipformerModelFiles(string Encoder, string Decoder, string Joiner, string Tokens);

/// <summary>
/// Загружает и держит потоковые модели Zipformer.
/// </summary>
/// <remarks>
/// Модель каждого языка создаётся один раз и живёт до выхода из приложения:
/// инициализация занимает секунды, а смена раскладки между фразами не должна
/// приводить к повторной загрузке.
///
/// Имена файлов внутри архивов моделей не унифицированы — встречаются и
/// «encoder.onnx», и «encoder-epoch-99-avg-1.int8.onnx». Поэтому файлы
/// ищутся по назначению, а не по точному имени; квантованные версии
/// предпочитаются как заметно более быстрые на обычном процессоре.
/// </remarks>
public sealed class ZipformerModelLoader : IDisposable
{
    private readonly Dictionary<RecognitionLanguage, OnlineRecognizer> _recognizers = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ILogger<ZipformerModelLoader> _logger;
    private bool _disposed;

    public ZipformerModelLoader(ILogger<ZipformerModelLoader>? logger = null) =>
        _logger = logger ?? NullLogger<ZipformerModelLoader>.Instance;

    public async Task<OnlineRecognizer> GetOrLoadAsync(
        RecognitionLanguage language,
        string modelPath,
        int threads,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new InvalidOperationException(
                $"Модель распознавания для {LanguageName(language)} языка не установлена. " +
                "Откройте настройки, вкладка «Модели», и скачайте её.");
        }

        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException(
                $"Модель для {LanguageName(language)} языка не найдена по пути {modelPath}. " +
                "Скачайте её заново в настройках, вкладка «Модели».");
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizers.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var files = ResolveFiles(modelPath, language);
            _logger.LogInformation("Загрузка модели Zipformer {Language} из {Path}", language, modelPath);

            var recognizer = await Task
                .Run(() => Create(files, threads), cancellationToken)
                .ConfigureAwait(false);

            _recognizers[language] = recognizer;
            return recognizer;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public bool IsLoaded(RecognitionLanguage language) => _recognizers.ContainsKey(language);

    /// <summary>Ищет файлы модели по назначению внутри каталога.</summary>
    public static ZipformerModelFiles ResolveFiles(string modelPath, RecognitionLanguage language)
    {
        var files = Directory.GetFiles(modelPath, "*.onnx", SearchOption.TopDirectoryOnly);

        var encoder = PickByPurpose(files, "encoder");
        var decoder = PickByPurpose(files, "decoder");
        var joiner = PickByPurpose(files, "joiner");
        var tokens = Path.Combine(modelPath, "tokens.txt");

        if (encoder is null || decoder is null || joiner is null || !File.Exists(tokens))
        {
            throw new InvalidOperationException(
                $"Каталог модели для {LanguageName(language)} языка неполон: нужны encoder, decoder, joiner и tokens.txt. " +
                "Скачайте модель заново в настройках, вкладка «Модели».");
        }

        return new ZipformerModelFiles(encoder, decoder, joiner, tokens);
    }

    private static string? PickByPurpose(string[] files, string purpose)
    {
        var candidates = files
            .Where(file => Path.GetFileName(file).Contains(purpose, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Квантованная версия быстрее на процессоре и заметно меньше, поэтому
        // при наличии обеих берётся она.
        return candidates.FirstOrDefault(file => file.Contains(".int8.", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    private static OnlineRecognizer Create(ZipformerModelFiles files, int threads)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioFormat.SampleRate;
        config.FeatConfig.FeatureDim = 80;

        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.NumThreads = Math.Max(1, threads);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        config.DecodingMethod = "greedy_search";

        // Определение конца фразы остаётся за VAD приложения: он видит звук
        // целиком и одинаково работает для обоих распознавателей. Здесь
        // endpoint выключен, иначе границы фраз считались бы дважды и по-разному.
        config.EnableEndpoint = 0;

        return new OnlineRecognizer(config);
    }

    private static string LanguageName(RecognitionLanguage language) => language switch
    {
        RecognitionLanguage.English => "английского",
        _ => "русского",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var recognizer in _recognizers.Values)
        {
            recognizer.Dispose();
        }

        _recognizers.Clear();
        _loadLock.Dispose();
    }
}
