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

    public AppSettings Load()
    {
        lock (_sync)
        {
            _current = ReadFromDisk();
            return _current;
        }
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

    private AppSettings ReadFromDisk()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettings();
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
            return new AppSettings();
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
