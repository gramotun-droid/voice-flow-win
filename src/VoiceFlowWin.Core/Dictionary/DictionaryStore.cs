using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Dictionary;

public interface IDictionaryStore
{
    IReadOnlyList<UserDictionaryEntry> Entries { get; }

    event EventHandler? DictionaryChanged;

    IReadOnlyList<UserDictionaryEntry> Load();

    void Save(IEnumerable<UserDictionaryEntry> entries);

    IReadOnlyList<UserDictionaryEntry> Import(string filePath, bool replaceExisting);

    void Export(string filePath);
}

/// <summary>Хранит словарь в dictionary.json рядом с настройками.</summary>
public sealed class DictionaryStore : IDictionaryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ILogger<DictionaryStore> _logger;
    private readonly object _sync = new();
    private List<UserDictionaryEntry> _entries = new();

    public DictionaryStore(AppPaths paths, ILogger<DictionaryStore>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<DictionaryStore>.Instance;
    }

    public IReadOnlyList<UserDictionaryEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList();
            }
        }
    }

    public event EventHandler? DictionaryChanged;

    public IReadOnlyList<UserDictionaryEntry> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_paths.DictionaryFile))
            {
                // Первый запуск: заполняем словарь набором частых терминов,
                // иначе пользователю пришлось бы вбивать их вручную.
                _entries = DefaultDictionary.Create();
                WriteUnsafe(_entries);
                return _entries.ToList();
            }

            try
            {
                var json = File.ReadAllText(_paths.DictionaryFile);
                _entries = JsonSerializer.Deserialize<List<UserDictionaryEntry>>(json, SerializerOptions) ?? new List<UserDictionaryEntry>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Словарь повреждён, загружается пустой список.");
                _entries = new List<UserDictionaryEntry>();
            }

            return _entries.ToList();
        }
    }

    public void Save(IEnumerable<UserDictionaryEntry> entries)
    {
        lock (_sync)
        {
            _entries = entries.ToList();
            WriteUnsafe(_entries);
        }

        DictionaryChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<UserDictionaryEntry> Import(string filePath, bool replaceExisting)
    {
        var json = File.ReadAllText(filePath);
        var imported = JsonSerializer.Deserialize<List<UserDictionaryEntry>>(json, SerializerOptions)
            ?? throw new InvalidDataException("Файл словаря не содержит записей.");

        List<UserDictionaryEntry> result;
        lock (_sync)
        {
            if (replaceExisting)
            {
                result = imported;
            }
            else
            {
                result = _entries.ToList();
                foreach (var entry in imported)
                {
                    var duplicate = result.FirstOrDefault(existing =>
                        string.Equals(existing.SpokenForm, entry.SpokenForm, StringComparison.OrdinalIgnoreCase));
                    if (duplicate is null)
                    {
                        result.Add(entry);
                    }
                    else
                    {
                        duplicate.Replacement = entry.Replacement;
                        duplicate.Variants = entry.Variants;
                        duplicate.Enabled = entry.Enabled;
                    }
                }
            }

            _entries = result;
            WriteUnsafe(_entries);
        }

        DictionaryChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Export(string filePath)
    {
        lock (_sync)
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(_entries, SerializerOptions));
        }
    }

    private void WriteUnsafe(List<UserDictionaryEntry> entries)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(entries, SerializerOptions);
        var tempFile = _paths.DictionaryFile + ".tmp";
        File.WriteAllText(tempFile, json);

        if (File.Exists(_paths.DictionaryFile))
        {
            File.Replace(tempFile, _paths.DictionaryFile, null);
        }
        else
        {
            File.Move(tempFile, _paths.DictionaryFile);
        }
    }
}
