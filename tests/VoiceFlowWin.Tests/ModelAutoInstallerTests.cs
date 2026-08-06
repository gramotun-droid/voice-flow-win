using System.Net.Http;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Проверяет автоматическую докачку моделей в той части, которая не требует
/// сети: выключенную настройку и уже установленный набор.
/// </summary>
public sealed class ModelAutoInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-autoinstall-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ModelManager _models;
    private readonly SettingsService _settings;

    public ModelAutoInstallerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _models = new ModelManager(new HttpClient(), _paths);
        _settings = new SettingsService(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Выключенная_настройка_ничего_не_качает()
    {
        var settings = _settings.Load();
        settings.Models.DownloadAllOnStartup = false;
        _settings.Save(settings);

        var installer = new ModelAutoInstaller(_models, _settings);

        Assert.Equal(0, await installer.InstallMissingAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(_paths.ModelsDirectory));
    }

    [Fact]
    public async Task Установленный_набор_не_перекачивается_и_прописывается_в_настройки()
    {
        foreach (var model in ModelCatalog.All)
        {
            PretendInstalled(model);
        }

        _settings.Load();
        var installer = new ModelAutoInstaller(_models, _settings);

        Assert.Equal(0, await installer.InstallMissingAsync(CancellationToken.None));

        var settings = new SettingsService(_paths).Load();
        Assert.NotEqual(string.Empty, settings.Streaming.RussianModelPath);
        Assert.NotEqual(string.Empty, settings.Streaming.EnglishModelPath);
        Assert.NotEqual(string.Empty, settings.Whisper.ModelPath);
    }

    [Fact]
    public async Task Отменённая_загрузка_не_начинается()
    {
        _settings.Load();
        var installer = new ModelAutoInstaller(_models, _settings);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(0, await installer.InstallMissingAsync(cancelled.Token));
    }

    private void PretendInstalled(ModelDescriptor model)
    {
        var path = _models.GetInstallPath(model);
        if (model.IsArchive)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "README"), "модель");
        }
        else
        {
            File.WriteAllText(path, "веса");
        }
    }
}
