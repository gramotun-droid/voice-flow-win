using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.SherpaEngine;
using VoiceFlowWin.Tests.Fakes;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Живая проверка потокового распознавания: модель скачивается, звук подаётся
/// кусками, как из микрофона, и текст должен совпасть с эталоном из архива
/// модели. Требует сети и 24 МБ трафика, поэтому в обычном прогоне
/// пропускается — запускать вручную при смене движка или модели.
/// </summary>
public sealed class ZipformerRecognitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-zipformer-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public ZipformerRecognitionTests()
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

    [Fact(Skip = "Требует сети и качает ~24 МБ; запускать вручную.")]
    public async Task Русская_речь_распознаётся_потоково()
    {
        var model = ModelCatalog.Find("sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16")!;
        var manager = new ModelManager(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, _paths);

        var install = await manager.InstallAsync(model, CancellationToken.None);
        Assert.True(install.Success, install.Error);

        var settings = new FakeSettingsService();
        settings.Mutate(current =>
        {
            current.Streaming.RussianModelPath = install.Path!;

            // Звук подаётся без пауз, а не в реальном времени, поэтому
            // ограничение частоты опроса гипотез здесь только мешало бы.
            current.Streaming.PartialResultIntervalMs = 0;
        });

        using var loader = new ZipformerModelLoader();
        using var recognizer = new StreamingZipformerRecognizer(loader, settings);

        await recognizer.PrepareAsync(RecognitionLanguage.Russian, CancellationToken.None);
        Assert.True(recognizer.IsReady);

        var partials = new List<string>();
        recognizer.PartialResult += (_, e) => partials.Add(e.Text);

        // Звук подаётся кусками по 100 мс — так же, как приходит с микрофона.
        var pcm = ReadWav(Path.Combine(install.Path!, "test_wavs", "0.wav"));
        const int chunk = 3200;
        for (var offset = 0; offset < pcm.Length; offset += chunk)
        {
            recognizer.AcceptAudio(pcm.AsSpan(offset, Math.Min(chunk, pcm.Length - offset)));
        }

        var final = recognizer.FlushFinalResult();

        Assert.Equal("я тебя люблю", final);
        Assert.NotEmpty(partials);

        // Гипотеза должна расти по мере речи, а не появляться целиком в конце.
        Assert.True(partials.Count > 1, "промежуточных гипотез должно быть несколько");

        // Гипотеза дополняется, а не переписывается с нуля.
        Assert.Equal(final, partials[^1]);
        Assert.StartsWith(partials[0], partials[^1], StringComparison.Ordinal);
    }

    /// <summary>WAV 16 kHz mono 16 bit: данные идут после 44-байтного заголовка.</summary>
    private static byte[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes[44..];
    }
}
