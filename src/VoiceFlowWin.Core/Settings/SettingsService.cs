using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoiceFlowWin.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// Читает и пишет settings.json.
/// </summary>
/// <remarks>
/// Запись атомарная: сначала во временный файл, затем замена. Иначе выключение
/// питания в момент сохранения оставляло бы пользователя с пустым файлом
/// настроек. Повреждённый файл не роняет приложение — он переименовывается
/// в .bad, а приложение стартует с настройками по умолчанию.
/// </remarks>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _sync = new();
    private AppSettings _current = new();

    public SettingsService(AppPaths paths, ILogger<SettingsService>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<SettingsService>.Instance;
    }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>Текущая версия схемы настроек.</summary>
    public const int CurrentSchemaVersion = 5;

    public AppSettings Load()
    {
        AppSettings settings;
        bool migrated;

        lock (_sync)
        {
            settings = ReadFromDisk();
            migrated = Migrate(settings);
            _current = settings;
        }

        // Перенос записывается сразу, иначе он повторялся бы при каждом запуске
        // и затирал бы сочетание, выбранное пользователем уже после переноса.
        if (migrated)
        {
            Save(settings);
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var tempFile = _paths.SettingsFile + ".tmp";
            File.WriteAllText(tempFile, json);

            if (File.Exists(_paths.SettingsFile))
            {
                File.Replace(tempFile, _paths.SettingsFile, null);
            }
            else
            {
                File.Move(tempFile, _paths.SettingsFile);
            }

            _current = settings;
        }

        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>Приводит настройки прежних версий к текущей схеме.</summary>
    /// <returns><c>true</c>, если что-то изменилось и настройки нужно сохранить.</returns>
    private bool Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return false;
        }

        // Версии до второй писали сочетание Ctrl + Alt + Space. Тому, кто его
        // не менял, отдаём новое умолчание Ctrl + F5.
        if (settings.SchemaVersion < 2 && settings.General.Hotkey == HotkeyDefinition.LegacyDefault)
        {
            settings.General.Hotkey = HotkeyDefinition.Default;
            _logger.LogInformation(
                "Сочетание вызова перенесено на {Hotkey}.",
                HotkeyDefinition.Default.ToDisplayString());
        }

        // До третьей версии текст вводился по мере речи, и это оказалось
        // источником почти всех проблем: промежуточные гипотезы в чужом поле,
        // потерянные окончания фраз, ложные признаки ручной правки. Тому, кто
        // режим не менял, отдаём вставку после паузы.
        if (settings.SchemaVersion < 3 && settings.General.LiveTextMode == LiveTextMode.SafeStreaming)
        {
            settings.General.LiveTextMode = LiveTextMode.InsertAfterPause;
            _logger.LogInformation("Режим ввода перенесён на вставку фразы после паузы.");
        }

        // Четвёртая версия сменила движок потокового распознавания на
        // sherpa-onnx. Модели Vosk ему не подходят, а пути к ним указывают на
        // каталоги, которых он не поймёт, — очищаем, чтобы новые модели
        // скачались сами.
        if (settings.SchemaVersion < 4)
        {
            settings.Streaming.RussianModelPath = string.Empty;
            settings.Streaming.EnglishModelPath = string.Empty;
            _logger.LogInformation("Пути потоковых моделей очищены: движок распознавания сменился.");
        }

        // В пятой версии фрагмент закрывается после пяти секунд тишины. Меняем
        // только прежнее значение по умолчанию: явно выбранную пользователем
        // длительность миграция не должна перезаписывать.
        if (settings.SchemaVersion < 5 && settings.Segmentation.SilenceToEndSegmentMs == 800)
        {
            settings.Segmentation.SilenceToEndSegmentMs = 5000;
            _logger.LogInformation("Пауза завершения фрагмента перенесена на 5000 мс.");
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    private AppSettings ReadFromDisk()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            // Новая установка сразу получает актуальную схему: переносить нечего.
            return new AppSettings { SchemaVersion = CurrentSchemaVersion };
        }

        try
        {
            var json = File.ReadAllText(_paths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Файл настроек повреждён или недоступен, используются значения по умолчанию.");
            TryQuarantine();
            return new AppSettings { SchemaVersion = CurrentSchemaVersion };
        }
    }

    private void TryQuarantine()
    {
        try
        {
            var badFile = _paths.SettingsFile + ".bad";
            File.Copy(_paths.SettingsFile, badFile, overwrite: true);
        }
        catch (IOException)
        {
            // Диагностическая копия не критична — молча продолжаем.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
