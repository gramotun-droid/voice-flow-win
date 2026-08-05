using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Models;
using VoskModel = Vosk.Model;

namespace VoiceFlowWin.VoskEngine;

/// <summary>
/// Загружает и держит модели Vosk.
/// </summary>
/// <remarks>
/// Загрузка модели занимает секунды и сотни мегабайт, поэтому модель каждого
/// языка создаётся один раз и живёт до выхода из приложения. Смена раскладки
/// между сегментами не должна приводить к повторной загрузке.
/// </remarks>
public sealed class VoskModelLoader : IDisposable
{
    private readonly Dictionary<RecognitionLanguage, VoskModel> _models = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ILogger<VoskModelLoader> _logger;
    private bool _disposed;

    static VoskModelLoader()
    {
        // Vosk пишет в stdout крайне подробный лог; в GUI-приложении он не нужен.
        global::Vosk.Vosk.SetLogLevel(-1);
    }

    public VoskModelLoader(ILogger<VoskModelLoader>? logger = null) =>
        _logger = logger ?? NullLogger<VoskModelLoader>.Instance;

    public async Task<VoskModel> GetOrLoadAsync(RecognitionLanguage language, string modelPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new InvalidOperationException($"Не задан путь к модели Vosk для языка {language}.");
        }

        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException($"Модель Vosk не найдена: {modelPath}");
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_models.TryGetValue(language, out var cached))
            {
                return cached;
            }

            _logger.LogInformation("Загрузка модели Vosk {Language} из {Path}", language, modelPath);
            var model = await Task.Run(() => new VoskModel(modelPath), cancellationToken).ConfigureAwait(false);
            _models[language] = model;
            return model;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public bool IsLoaded(RecognitionLanguage language) => _models.ContainsKey(language);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var model in _models.Values)
        {
            model.Dispose();
        }

        _models.Clear();
        _loadLock.Dispose();
    }
}
