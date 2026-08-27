using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.History;

public sealed record DictationSessionSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RecognitionLanguage Language,
    string Text,
    byte[] Pcm);

public sealed record DictationHistoryRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    RecognitionLanguage Language,
    int TextLength,
    string? Text,
    string? AudioFile);

public interface IDictationHistoryStore
{
    Task SaveSessionAsync(
        DictationSessionSnapshot session,
        PrivacySettings privacy,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DictationHistoryRecord>> LoadAsync(CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Локальная история диктовок и опциональные WAV-записи сеансов.</summary>
/// <remarks>
/// История включается одним переключателем <see cref="PrivacySettings.KeepHistory"/>.
/// Текст и звук сохраняются только при включённых дочерних настройках. JSON
/// заменяется атомарно, имена WAV генерируются приложением и при очистке не
/// могут адресовать файл за пределами каталога истории.
/// </remarks>
public sealed class DictationHistoryStore : IDictationHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<DictationHistoryStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);

    public DictationHistoryStore(AppPaths paths, ILogger<DictationHistoryStore>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<DictationHistoryStore>.Instance;
    }

    public async Task SaveSessionAsync(
        DictationSessionSnapshot session,
        PrivacySettings privacy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(privacy);

        if (!privacy.KeepHistory)
        {
            return;
        }

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var records = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PruneExpiredUnsafe(records, privacy.HistoryRetentionDays, DateTimeOffset.UtcNow);

            var id = Guid.NewGuid();
            string? audioFile = null;
            if (privacy.StoreAudio && session.Pcm.Length > 0)
            {
                Directory.CreateDirectory(_paths.HistoryAudioDirectory);
                audioFile = $"{session.EndedAt:yyyyMMdd-HHmmss}-{id:N}.wav";
                await WriteWaveUnsafeAsync(
                    Path.Combine(_paths.HistoryAudioDirectory, audioFile),
                    session.Pcm,
                    cancellationToken).ConfigureAwait(false);
            }

            records.Add(new DictationHistoryRecord(
                id,
                session.StartedAt,
                session.EndedAt,
                session.Language,
                session.Text.Length,
                privacy.StoreRecognizedText ? session.Text : null,
                audioFile));

            try
            {
                await WriteUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                DeleteAudioUnsafe(audioFile);
                throw;
            }

            _logger.LogInformation(
                "Сеанс диктовки добавлен в локальную историю: текст {TextStored}, аудио {AudioStored}.",
                privacy.StoreRecognizedText,
                audioFile is not null);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<DictationHistoryRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.HistoryFile))
            {
                File.Delete(_paths.HistoryFile);
            }

            if (Directory.Exists(_paths.HistoryAudioDirectory))
            {
                Directory.Delete(_paths.HistoryAudioDirectory, recursive: true);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<List<DictationHistoryRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.HistoryFile))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_paths.HistoryFile);
            return await JsonSerializer
                .DeserializeAsync<List<DictationHistoryRecord>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "История диктовок повреждена или недоступна; файл изолирован.");
            TryQuarantineUnsafe();
            return [];
        }
    }

    private async Task WriteUnsafeAsync(
        IReadOnlyList<DictationHistoryRecord> records,
        CancellationToken cancellationToken)
    {
        var tempFile = _paths.HistoryFile + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, records, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempFile, _paths.HistoryFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private void PruneExpiredUnsafe(
        List<DictationHistoryRecord> records,
        int retentionDays,
        DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        foreach (var expired in records.Where(record => record.EndedAt < cutoff).ToArray())
        {
            DeleteAudioUnsafe(expired.AudioFile);
            records.Remove(expired);
        }
    }

    private void DeleteAudioUnsafe(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return;
        }

        var path = Path.Combine(_paths.HistoryAudioDirectory, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryQuarantineUnsafe()
    {
        try
        {
            File.Move(_paths.HistoryFile, _paths.HistoryFile + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Повреждённую историю не удалось изолировать.");
        }
    }

    private static async Task WriteWaveUnsafeAsync(
        string path,
        byte[] pcm,
        CancellationToken cancellationToken)
    {
        var tempFile = path + ".tmp";
        var header = CreateWaveHeader(pcm.Length);
        try
        {
            await using (var stream = new FileStream(
                tempFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempFile, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static byte[] CreateWaveHeader(int pcmLength)
    {
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + pcmLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), AudioFormat.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), AudioFormat.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(28),
            AudioFormat.SampleRate * AudioFormat.Channels * AudioFormat.BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(
            header.AsSpan(32),
            AudioFormat.Channels * AudioFormat.BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), AudioFormat.BitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), pcmLength);
        return header;
    }
}
