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
        InstallVosk("vosk-model-small-ru-0.22");
        InstallVosk("vosk-model-small-en-us-0.15");
        InstallWhisper("ggml-small-q5_1");

        var settings = new AppSettings();
        var changed = _manager.SynchronizeInstalledPaths(settings);

        Assert.True(changed);
        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "vosk-model-small-ru-0.22"), settings.Vosk.RussianModelPath);
        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "vosk-model-small-en-us-0.15"), settings.Vosk.EnglishModelPath);
        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "ggml-small-q5_1.bin"), settings.Whisper.ModelPath);
        Assert.Equal("ggml-small-q5_1", settings.Whisper.ModelId);
    }

    [Fact]
    public void Путь_исчезнувшей_модели_очищается()
    {
        var settings = new AppSettings();
        settings.Vosk.RussianModelPath = Path.Combine(_paths.ModelsDirectory, "vosk-model-small-ru-0.22");
        settings.Whisper.ModelPath = Path.Combine(_paths.ModelsDirectory, "ggml-small-q5_1.bin");
        settings.Whisper.ModelId = "ggml-small-q5_1";

        var changed = _manager.SynchronizeInstalledPaths(settings);

        Assert.True(changed);
        Assert.Equal(string.Empty, settings.Vosk.RussianModelPath);
        Assert.Equal(string.Empty, settings.Whisper.ModelPath);
        Assert.Equal(string.Empty, settings.Whisper.ModelId);
    }

    [Fact]
    public void Выбранная_пользователем_модель_не_подменяется()
    {
        InstallVosk("vosk-model-small-ru-0.22");
        InstallVosk("vosk-model-ru-0.42");

        var settings = new AppSettings();
        settings.Vosk.RussianModelPath = Path.Combine(_paths.ModelsDirectory, "vosk-model-ru-0.42");

        _manager.SynchronizeInstalledPaths(settings);

        Assert.Equal(Path.Combine(_paths.ModelsDirectory, "vosk-model-ru-0.42"), settings.Vosk.RussianModelPath);
    }

    [Fact]
    public void Настройки_без_моделей_остаются_без_изменений()
    {
        var settings = new AppSettings();

        Assert.False(_manager.SynchronizeInstalledPaths(settings));
        Assert.Equal(string.Empty, settings.Vosk.RussianModelPath);
    }

    private void InstallVosk(string id)
    {
        var directory = Path.Combine(_paths.ModelsDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README"), "модель");
    }

    private void InstallWhisper(string id) =>
        File.WriteAllText(Path.Combine(_paths.ModelsDirectory, id + ".bin"), "веса");
}
