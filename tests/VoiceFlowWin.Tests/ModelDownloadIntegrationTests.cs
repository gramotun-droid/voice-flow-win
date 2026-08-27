using System.Net.Http;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.SherpaEngine;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Живая проверка загрузки модели: адреса каталога, докачка, контрольная сумма
/// и распаковка. Требует сети и десятков мегабайт трафика, поэтому в обычном
/// прогоне пропускается — запускать вручную при смене источников моделей.
/// </summary>
public sealed class ModelDownloadIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-download-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public ModelDownloadIntegrationTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [ModelIntegrationFact]
    public async Task Английская_Zipformer_модель_скачивается_и_проходит_проверку()
    {
        var model = ModelCatalog.Find("sherpa-onnx-streaming-zipformer-en-20M-2023-02-17")!;
        var manager = new ModelManager(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, _paths);

        var stages = new List<string>();
        manager.Progress += (_, e) => stages.Add(e.Stage);

        var result = await manager.InstallAsync(model, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.True(manager.IsInstalled(model));
        Assert.Contains(stages, stage => stage.StartsWith("Загрузка", StringComparison.Ordinal));

        var files = ZipformerModelLoader.ResolveFiles(result.Path!, RecognitionLanguage.English);
        Assert.True(File.Exists(files.Encoder));
        Assert.True(File.Exists(files.Decoder));
        Assert.True(File.Exists(files.Joiner));
        Assert.True(File.Exists(files.Tokens));
    }
}
