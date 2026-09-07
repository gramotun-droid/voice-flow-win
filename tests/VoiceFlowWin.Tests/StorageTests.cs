using System.IO.Compression;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-storage-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public StorageTests()
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

    [Fact]
    public void Настройки_сохраняются_и_читаются()
    {
        var service = new SettingsService(_paths);
        var settings = service.Load();
        settings.General.ActivationMode = DictationActivationMode.PushToTalk;
        settings.General.Hotkey = new HotkeyDefinition(0x72, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        settings.Streaming.StableRepeats = 4;

        service.Save(settings);
        var restored = new SettingsService(_paths).Load();

        Assert.Equal(DictationActivationMode.PushToTalk, restored.General.ActivationMode);
        Assert.Equal(0x72, restored.General.Hotkey.VirtualKey);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, restored.General.Hotkey.Modifiers);
        Assert.Equal(4, restored.Streaming.StableRepeats);
    }

    [Fact]
    public void Повреждённый_файл_настроек_не_роняет_приложение()
    {
        File.WriteAllText(_paths.SettingsFile, "{ это не настройки");

        var settings = new SettingsService(_paths).Load();

        // Приложение стартует со значениями по умолчанию.
        Assert.Equal(DictationActivationMode.Toggle, settings.General.ActivationMode);
        Assert.True(File.Exists(_paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Настройки_по_умолчанию_соответствуют_требованиям()
    {
        var settings = new AppSettings();

        Assert.Equal(HotkeyDefinition.Default, settings.General.Hotkey);
        Assert.Equal(EscapeBehavior.FinalizeAndKeep, settings.General.EscapeBehavior);
        // Единственный режим — текущая потоковая гипотеза сразу в поле.
        Assert.Equal(LiveTextMode.MaximumLive, settings.General.LiveTextMode);
        Assert.Equal(LanguageSelectionMode.FollowKeyboardLayout, settings.General.LanguageMode);

        // Конфиденциальность: по умолчанию ничего не хранится.
        Assert.False(settings.Privacy.StoreAudio);
        Assert.False(settings.Privacy.StoreRecognizedText);
        Assert.False(settings.Privacy.KeepHistory);

        // Пауза завершения фразы и максимальная длительность из ТЗ.
        Assert.Equal(5000, settings.Segmentation.SilenceToEndSegmentMs);
        Assert.InRange(settings.Segmentation.MaxSegmentSeconds, 20, 30);
        Assert.Equal(4, settings.Updates.CheckIntervalHours);
    }

    [Fact]
    public void Словарь_создаётся_с_набором_по_умолчанию()
    {
        var store = new DictionaryStore(_paths);

        var entries = store.Load();

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.Replacement == "GitHub");
        Assert.True(File.Exists(_paths.DictionaryFile));
    }

    [Fact]
    public void Словарь_экспортируется_и_импортируется()
    {
        var store = new DictionaryStore(_paths);
        store.Load();
        store.Save(new[] { new UserDictionaryEntry { SpokenForm = "тест", Replacement = "Test" } });

        var exportPath = Path.Combine(_root, "export.json");
        store.Export(exportPath);

        var other = new DictionaryStore(new AppPaths(Path.Combine(_root, "other")));
        var imported = other.Import(exportPath, replaceExisting: true);

        Assert.Single(imported);
        Assert.Equal("Test", imported[0].Replacement);
    }

    [Fact]
    public void Архив_модели_распаковывается()
    {
        var archive = CreateArchive(("vosk-model/README", "модель"), ("vosk-model/am/final.mdl", "данные"));
        var target = Path.Combine(_root, "model");

        ModelManager.ExtractArchiveSafely(archive, target);

        // Один верхний каталог поднимается наверх, чтобы путь указывал на модель.
        Assert.True(File.Exists(Path.Combine(target, "README")));
        Assert.True(File.Exists(Path.Combine(target, "am", "final.mdl")));
    }

    [Fact]
    public void Архив_с_обходом_каталога_отклоняется()
    {
        var archive = CreateArchive(("../../evil.txt", "вредно"));
        var target = Path.Combine(_root, "model-evil");

        var error = Assert.Throws<InvalidDataException>(() => ModelManager.ExtractArchiveSafely(archive, target));

        Assert.Contains("за пределы каталога", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "..", "evil.txt")));
    }

    [Fact]
    public void Каталог_данных_собирается_из_корня()
    {
        Assert.StartsWith(_root, _paths.ModelsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(_root, _paths.UpdatesDirectory, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_paths.LogsDirectory));
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }
}
