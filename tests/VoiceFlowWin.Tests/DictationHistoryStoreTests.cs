using System.Buffers.Binary;
using System.Text;
using VoiceFlowWin.Core.History;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

public sealed class DictationHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-history-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly DictationHistoryStore _store;

    public DictationHistoryStoreTests()
    {
        _paths = new AppPaths(_root);
        _store = new DictationHistoryStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Выключенная_история_ничего_не_записывает()
    {
        await _store.SaveSessionAsync(CreateSession(), new PrivacySettings(), CancellationToken.None);

        Assert.False(File.Exists(_paths.HistoryFile));
        Assert.False(Directory.Exists(_paths.HistoryAudioDirectory));
    }

    [Fact]
    public async Task История_атомарно_сохраняет_текст_и_Wav()
    {
        var privacy = new PrivacySettings
        {
            KeepHistory = true,
            StoreRecognizedText = true,
            StoreAudio = true,
        };

        await _store.SaveSessionAsync(CreateSession(), privacy, CancellationToken.None);

        var record = Assert.Single(await _store.LoadAsync(CancellationToken.None));
        Assert.Equal("Привет, мир.", record.Text);
        Assert.Equal(12, record.TextLength);
        Assert.NotNull(record.AudioFile);

        var audioPath = Path.Combine(_paths.HistoryAudioDirectory, record.AudioFile!);
        var wav = await File.ReadAllBytesAsync(audioPath);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));
    }

    [Fact]
    public async Task Текст_и_аудио_не_попадают_в_метаданные_без_явного_согласия()
    {
        var privacy = new PrivacySettings { KeepHistory = true };

        await _store.SaveSessionAsync(CreateSession(), privacy, CancellationToken.None);

        var record = Assert.Single(await _store.LoadAsync(CancellationToken.None));
        Assert.Null(record.Text);
        Assert.Null(record.AudioFile);
        Assert.Equal(12, record.TextLength);
    }

    [Fact]
    public async Task Просроченная_запись_и_её_аудио_удаляются()
    {
        var privacy = new PrivacySettings
        {
            KeepHistory = true,
            StoreAudio = true,
            HistoryRetentionDays = 1,
        };

        var old = CreateSession(DateTimeOffset.UtcNow.AddDays(-10));
        await _store.SaveSessionAsync(old, privacy, CancellationToken.None);
        var oldRecord = Assert.Single(await _store.LoadAsync(CancellationToken.None));
        var oldAudio = Path.Combine(_paths.HistoryAudioDirectory, oldRecord.AudioFile!);
        Assert.True(File.Exists(oldAudio));

        await _store.SaveSessionAsync(CreateSession(), privacy, CancellationToken.None);

        var remaining = Assert.Single(await _store.LoadAsync(CancellationToken.None));
        Assert.NotEqual(oldRecord.Id, remaining.Id);
        Assert.False(File.Exists(oldAudio));
    }

    [Fact]
    public async Task Очистка_удаляет_метаданные_и_аудио()
    {
        var privacy = new PrivacySettings
        {
            KeepHistory = true,
            StoreRecognizedText = true,
            StoreAudio = true,
        };

        await _store.SaveSessionAsync(CreateSession(), privacy, CancellationToken.None);
        await _store.ClearAsync(CancellationToken.None);

        Assert.False(File.Exists(_paths.HistoryFile));
        Assert.False(Directory.Exists(_paths.HistoryAudioDirectory));
    }

    [Fact]
    public async Task Повреждённая_история_изолируется()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(_paths.HistoryFile, "{не json");

        var records = await _store.LoadAsync(CancellationToken.None);

        Assert.Empty(records);
        Assert.True(File.Exists(_paths.HistoryFile + ".bad"));
    }

    private static DictationSessionSnapshot CreateSession(DateTimeOffset? endedAt = null)
    {
        var end = endedAt ?? DateTimeOffset.UtcNow;
        return new DictationSessionSnapshot(
            end.AddSeconds(-1),
            end,
            RecognitionLanguage.Russian,
            "Привет, мир.",
            Enumerable.Range(0, 3200).Select(index => (byte)(index % 255)).ToArray());
    }
}
