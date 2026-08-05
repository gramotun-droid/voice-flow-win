using VoiceFlowWin.Core.Models;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Каталог моделей — это внешние адреса и контрольные суммы, поэтому ошибка в
/// нём проявляется только у пользователя. Тесты фиксируют инварианты, которые
/// можно проверить без сети.
/// </summary>
public class ModelCatalogTests
{
    [Fact]
    public void Все_адреса_только_по_HTTPS()
    {
        foreach (var model in ModelCatalog.All)
        {
            foreach (var url in model.DownloadUrls)
            {
                Assert.StartsWith("https://", url, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void У_рекомендованных_моделей_есть_запасной_источник_и_контрольная_сумма()
    {
        // Рекомендованные модели скачиваются автоматически: молчаливый обрыв
        // единственного источника оставил бы приложение неработоспособным.
        foreach (var model in ModelCatalog.Recommended)
        {
            Assert.True(model.DownloadUrls.Count() > 1, $"{model.Id}: нет запасного источника");
            Assert.False(string.IsNullOrWhiteSpace(model.Sha256), $"{model.Id}: нет контрольной суммы");
        }
    }

    [Fact]
    public void Контрольные_суммы_записаны_шестнадцатеричными_числами()
    {
        foreach (var model in ModelCatalog.All.Where(item => item.Sha256 is not null))
        {
            Assert.Equal(64, model.Sha256!.Length);
            Assert.All(model.Sha256, symbol => Assert.True(Uri.IsHexDigit(symbol)));
        }
    }

    [Fact]
    public void Источники_модели_не_повторяются()
    {
        foreach (var model in ModelCatalog.All)
        {
            var urls = model.DownloadUrls.ToList();
            Assert.Equal(urls.Count, urls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Есть_по_модели_на_каждую_роль()
    {
        Assert.Contains(ModelCatalog.Recommended, model => model.Kind == ModelKind.Vosk && model.Language == RecognitionLanguage.Russian);
        Assert.Contains(ModelCatalog.Recommended, model => model.Kind == ModelKind.Vosk && model.Language == RecognitionLanguage.English);
        Assert.Contains(ModelCatalog.Recommended, model => model.Kind == ModelKind.Whisper);
    }
}
