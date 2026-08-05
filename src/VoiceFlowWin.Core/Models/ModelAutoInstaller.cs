using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Models;

/// <summary>
/// Докачивает недостающие модели после запуска приложения.
/// </summary>
/// <remarks>
/// Без моделей приложение бесполезно, а выбирать их при первом запуске должен
/// не каждый пользователь. Поэтому набор скачивается сам, в фоне и по одной
/// модели за раз: параллельная загрузка нескольких сотен мегабайт только
/// отнимала бы канал у той модели, которая нужна раньше всех.
///
/// Порядок важен: сначала рекомендованные, чтобы диктовка заработала после
/// первых сотен мегабайт, а тяжёлые модели догружались уже во время работы.
/// Ошибка загрузки не прерывает остальные: отсутствие сети — обычное дело,
/// проверка повторится при следующем запуске.
/// </remarks>
public sealed class ModelAutoInstaller
{
    private readonly ModelManager _models;
    private readonly ISettingsService _settings;
    private readonly ILogger<ModelAutoInstaller> _logger;

    public ModelAutoInstaller(
        ModelManager models,
        ISettingsService settings,
        ILogger<ModelAutoInstaller>? logger = null)
    {
        _models = models;
        _settings = settings;
        _logger = logger ?? NullLogger<ModelAutoInstaller>.Instance;
    }

    /// <summary>Скачивает всё, чего нет на диске. Возвращает число установленных моделей.</summary>
    public async Task<int> InstallMissingAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Current.Models.DownloadAllOnStartup)
        {
            return 0;
        }

        var installed = 0;

        foreach (var model in ModelCatalog.All.OrderByDescending(item => item.Recommended))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (_models.IsInstalled(model))
            {
                continue;
            }

            try
            {
                var result = await _models.InstallAsync(model, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("Модель {Model} не скачана: {Error}", model.Id, result.Error);
                    continue;
                }

                installed++;
                _logger.LogInformation("Модель {Model} установлена автоматически.", model.Id);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Путь прописывается сразу после каждой модели: диктовка должна
            // стать доступной, не дожидаясь конца всей загрузки.
            SynchronizePaths();
        }

        // Финальная сверка нужна и когда качать было нечего: модели могли
        // появиться на диске мимо приложения — например, при переустановке.
        SynchronizePaths();

        return installed;
    }

    private void SynchronizePaths()
    {
        var settings = _settings.Current;
        if (_models.SynchronizeInstalledPaths(settings))
        {
            _settings.Save(settings);
        }
    }
}
