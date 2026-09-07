using System.Net.Http;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Проверяет синхронизацию путей моделей с содержимым каталога моделей:
/// скачанная модель должна работать без ручной правки настроек, а удалённая —
/// не оставлять после себя нерабочий путь.
/// </summary>
public sealed class ModelPathSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-models-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ModelManager _manager;

    public ModelPathSyncTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _manager = new ModelManager(new HttpClient(), _paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Пути_установленных_моделей_прописываются_в_пустые_настройки()
    {
        InstallStreaming("sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16");
        InstallStreaming("sherpa-onnx-streaming-zipformer-en-20M-2023-02-17");

        var settings = new AppSettings();
        var changed = _manager.SynchronizeInstalledPaths(settings);

        Assert.True(changed);
        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16"), settings.Streaming.RussianModelPath);
        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17"), settings.Streaming.EnglishModelPath);
        Assert.Equal(string.Empty, settings.Whisper.ModelPath);
    }

    [Fact]
    public void Путь_исчезнувшей_модели_очищается()
    {
        var settings = new AppSettings();
        settings.Streaming.RussianModelPath = Path.Combine(_paths.ModelsDirectory, "sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16");
        settings.Whisper.ModelPath = Path.Combine(_paths.ModelsDirectory, "ggml-small-q5_1.bin");
        settings.Whisper.ModelId = "ggml-small-q5_1";

        var changed = _manager.SynchronizeInstalledPaths(settings);

        Assert.True(changed);
        Assert.Equal(string.Empty, settings.Streaming.RussianModelPath);
        Assert.Equal(string.Empty, settings.Whisper.ModelPath);
        Assert.Equal(string.Empty, settings.Whisper.ModelId);
    }

    [Fact]
    public void Выбранная_пользователем_модель_не_подменяется()
    {
        InstallStreaming("sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16");
        var custom = Path.Combine(_paths.ModelsDirectory, "своя-модель");
        Directory.CreateDirectory(custom);

        var settings = new AppSettings();
        settings.Streaming.RussianModelPath = custom;

        _manager.SynchronizeInstalledPaths(settings);

        Assert.Equal(custom, settings.Streaming.RussianModelPath);
    }

    [Fact]
    public void Настройки_без_моделей_остаются_без_изменений()
    {
        var settings = new AppSettings();

        Assert.False(_manager.SynchronizeInstalledPaths(settings));
        Assert.Equal(string.Empty, settings.Streaming.RussianModelPath);
    }

    private void InstallStreaming(string id)
    {
        var directory = Path.Combine(_paths.ModelsDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README"), "модель");
    }
}
