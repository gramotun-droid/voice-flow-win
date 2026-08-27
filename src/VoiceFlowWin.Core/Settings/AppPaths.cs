namespace VoiceFlowWin.Core.Settings;

/// <summary>
/// Единое место, где приложение хранит данные пользователя.
/// </summary>
/// <remarks>
/// Всё лежит в %LocalAppData%\VoiceFlowWin. Установщик и обновление не трогают
/// этот каталог, поэтому настройки, словарь и скачанные модели переживают
/// переустановку. На не-Windows пути строятся через те же API, чтобы тесты
/// проходили на ubuntu-latest.
/// </remarks>
public sealed class AppPaths
{
    public const string ProductFolderName = "VoiceFlowWin";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductFolderName);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string DictionaryFile => Path.Combine(Root, "dictionary.json");

    public string ModelsDirectory => Path.Combine(Root, "Models");

    public string UpdatesDirectory => Path.Combine(Root, "Updates");

    public string LogsDirectory => Path.Combine(Root, "Logs");

    public string HistoryFile => Path.Combine(Root, "history.json");

    public string HistoryAudioDirectory => Path.Combine(Root, "HistoryAudio");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
