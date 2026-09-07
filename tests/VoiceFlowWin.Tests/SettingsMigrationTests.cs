using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Проверяет перенос настроек прежних версий: умолчание меняется, осознанный
/// выбор пользователя — нет.
/// </summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-migration-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SettingsMigrationTests()
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
    public void Прежнее_умолчание_переносится_на_Ctrl_F5()
    {
        WriteLegacySettings(virtualKey: 0x20, modifiers: "Control, Alt");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(HotkeyDefinition.Default, settings.General.Hotkey);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Выбранное_пользователем_сочетание_не_меняется()
    {
        WriteLegacySettings(virtualKey: 0x72, modifiers: "Control, Shift");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(0x72, settings.General.Hotkey.VirtualKey);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, settings.General.Hotkey.Modifiers);
    }

    [Fact]
    public void Перенос_записывается_на_диск_и_не_повторяется()
    {
        WriteLegacySettings(virtualKey: 0x20, modifiers: "Control, Alt");
        new SettingsService(_paths).Load();

        // После переноса пользователь сознательно выбирает прежнее сочетание —
        // повторный запуск не имеет права его вернуть к умолчанию.
        var service = new SettingsService(_paths);
        var settings = service.Load();
        settings.General.Hotkey = HotkeyDefinition.LegacyDefault;
        service.Save(settings);

        var reloaded = new SettingsService(_paths).Load();

        Assert.Equal(HotkeyDefinition.LegacyDefault, reloaded.General.Hotkey);
    }

    [Fact]
    public void Прежний_режим_ввода_переносится_на_единый_потоковый_набор()
    {
        WriteSettings("""{ "schemaVersion": 2, "general": { "liveTextMode": "SafeStreaming" } }""");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(LiveTextMode.MaximumLive, settings.General.LiveTextMode);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Любой_прежний_режим_ввода_заменяется_единым_потоковым()
    {
        WriteSettings("""{ "schemaVersion": 2, "general": { "liveTextMode": "MaximumLive" } }""");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(LiveTextMode.MaximumLive, settings.General.LiveTextMode);
    }

    [Fact]
    public void Перенос_режима_не_трогает_уже_перенесённое_сочетание()
    {
        // Файл второй версии сочетание уже получил, второй раз его менять
        // нельзя: пользователь мог сознательно вернуть прежнее.
        WriteSettings("""
            {
              "schemaVersion": 2,
              "general": {
                "hotkey": { "virtualKey": 32, "modifiers": "Control, Alt" },
                "liveTextMode": "SafeStreaming"
              }
            }
            """);

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(HotkeyDefinition.LegacyDefault, settings.General.Hotkey);
        Assert.Equal(LiveTextMode.MaximumLive, settings.General.LiveTextMode);
    }

    [Fact]
    public void Прежняя_пауза_по_умолчанию_переносится_на_пять_секунд()
    {
        WriteSettings("""{ "schemaVersion": 4, "segmentation": { "silenceToEndSegmentMs": 800 } }""");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(5000, settings.Segmentation.SilenceToEndSegmentMs);
        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Любая_пауза_прежней_схемы_переносится_на_пять_секунд()
    {
        WriteSettings("""{ "schemaVersion": 5, "segmentation": { "silenceToEndSegmentMs": 2500 } }""");

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(5000, settings.Segmentation.SilenceToEndSegmentMs);
    }

    [Fact]
    public void Финализация_и_автоязык_Whisper_отключаются_при_переносе()
    {
        WriteSettings("""
            {
              "schemaVersion": 6,
              "general": { "languageMode": "WhisperAutoDetect" },
              "whisper": { "fullPassAfterStop": true },
              "injection": { "safeFinalReplacement": true }
            }
            """);

        var settings = new SettingsService(_paths).Load();

        Assert.Equal(LanguageSelectionMode.FollowKeyboardLayout, settings.General.LanguageMode);
        Assert.False(settings.Whisper.FullPassAfterStop);
        Assert.False(settings.Injection.SafeFinalReplacement);
    }

    [Fact]
    public void Новая_установка_сразу_на_текущей_схеме()
    {
        var settings = new SettingsService(_paths).Load();

        Assert.Equal(SettingsService.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(HotkeyDefinition.Default, settings.General.Hotkey);
    }

    private void WriteSettings(string json) => File.WriteAllText(_paths.SettingsFile, json);

    private void WriteLegacySettings(int virtualKey, string modifiers) =>
        File.WriteAllText(
            _paths.SettingsFile,
            $$"""
            {
              "general": {
                "activationMode": "Toggle",
                "hotkey": { "virtualKey": {{virtualKey}}, "modifiers": "{{modifiers}}" }
              }
            }
            """);
}
