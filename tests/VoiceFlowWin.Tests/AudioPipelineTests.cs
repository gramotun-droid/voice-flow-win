using VoiceFlowWin.Audio;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Settings;
using Xunit;

namespace VoiceFlowWin.Tests;

public class AudioPipelineTests
{
    /// <summary>Синусоида как заменитель речи: у неё предсказуемые RMS и частота переходов через ноль.</summary>
    private static byte[] Tone(int milliseconds, double amplitude = 0.3, double frequency = 220)
    {
        var sampleCount = AudioFormat.SampleRate * milliseconds / 1000;
        var result = new byte[sampleCount * AudioFormat.BytesPerSample];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = Math.Sin(2 * Math.PI * frequency * i / AudioFormat.SampleRate) * amplitude;
            BitConverter.TryWriteBytes(result.AsSpan(i * 2, 2), (short)(value * short.MaxValue));
        }

        return result;
    }

    private static byte[] Silence(int milliseconds) =>
        new byte[AudioFormat.BytesFromMilliseconds(milliseconds)];

    [Fact]
    public void Кольцевой_буфер_хранит_последние_миллисекунды()
    {
        var buffer = new RingAudioBuffer(capacityMilliseconds: 300);

        buffer.Write(Tone(500));

        Assert.Equal(300, buffer.LengthMilliseconds);
        Assert.Equal(AudioFormat.BytesFromMilliseconds(200), buffer.ReadLast(200).Length);
    }

    [Fact]
    public void Кольцевой_буфер_переживает_обёртывание()
    {
        var buffer = new RingAudioBuffer(capacityMilliseconds: 100);

        // Пишем по кусочку, пересекая границу буфера несколько раз.
        for (var i = 0; i < 10; i++)
        {
            buffer.Write(Tone(30));
        }

        var last = buffer.ReadLast(100);

        Assert.Equal(AudioFormat.BytesFromMilliseconds(100), last.Length);
        Assert.Contains(last, b => b != 0);
    }

    [Fact]
    public void Очистка_буфера_стирает_звук()
    {
        var buffer = new RingAudioBuffer(200);
        buffer.Write(Tone(200));

        buffer.Clear();

        Assert.Equal(0, buffer.Length);
        Assert.Empty(buffer.ReadLast(200));
    }

    [Fact]
    public void Детектор_отличает_тишину_от_сигнала()
    {
        var detector = new EnergyVoiceActivityDetector(sensitivity: 0.5);
        var frame = AudioFormat.BytesFromMilliseconds(detector.FrameMilliseconds);

        // Прогреваем уровень шума тишиной.
        var silence = Silence(200);
        for (var offset = 0; offset + frame <= silence.Length; offset += frame)
        {
            detector.Process(silence.AsSpan(offset, frame));
        }

        var tone = Tone(200);
        var speechFrames = 0;
        for (var offset = 0; offset + frame <= tone.Length; offset += frame)
        {
            if (detector.Process(tone.AsSpan(offset, frame)).IsSpeech)
            {
                speechFrames++;
            }
        }

        Assert.True(speechFrames > 5, $"Ожидалась речь, распознано кадров: {speechFrames}");
    }

    [Fact]
    public void Сегментатор_закрывает_сегмент_после_паузы()
    {
        var settings = new SegmentationSettings
        {
            SilenceToEndSegmentMs = 300,
            MinSpeechMs = 100,
            PreRollMs = 100,
            PostRollMs = 100,
        };
        var segmenter = new SpeechSegmenter(new EnergyVoiceActivityDetector(0.5), settings);

        SpeechSegmentEventArgs? completed = null;
        var started = 0;
        segmenter.SpeechStarted += (_, _) => started++;
        segmenter.SegmentCompleted += (_, args) => completed = args;

        segmenter.Push(Silence(300));
        segmenter.Push(Tone(600));
        segmenter.Push(Silence(600));

        Assert.Equal(1, started);
        Assert.NotNull(completed);
        Assert.Equal(SegmentEndReason.Silence, completed!.Reason);

        // Сегмент включает pre-roll и обрезанную паузу, но не всю тишину.
        Assert.True(completed.DurationMilliseconds > 600, $"Длительность {completed.DurationMilliseconds} мс");
        Assert.True(completed.DurationMilliseconds < 1100, $"Длительность {completed.DurationMilliseconds} мс");
    }

    [Fact]
    public void Сегментатор_отбрасывает_слишком_короткий_всплеск()
    {
        var settings = new SegmentationSettings { SilenceToEndSegmentMs = 200, MinSpeechMs = 400 };
        var segmenter = new SpeechSegmenter(new EnergyVoiceActivityDetector(0.5), settings);

        var completed = 0;
        segmenter.SegmentCompleted += (_, _) => completed++;

        segmenter.Push(Silence(300));
        segmenter.Push(Tone(80));
        segmenter.Push(Silence(500));

        Assert.Equal(0, completed);
    }

    [Fact]
    public void Сегментатор_режет_слишком_длинную_речь()
    {
        var settings = new SegmentationSettings
        {
            MaxSegmentSeconds = 1,
            SilenceToEndSegmentMs = 5000,
            MinSpeechMs = 100,
        };
        var segmenter = new SpeechSegmenter(new EnergyVoiceActivityDetector(0.5), settings);

        var reasons = new List<SegmentEndReason>();
        segmenter.SegmentCompleted += (_, args) => reasons.Add(args.Reason);

        segmenter.Push(Silence(300));
        segmenter.Push(Tone(2500));

        Assert.Contains(SegmentEndReason.MaxDuration, reasons);
    }

    [Fact]
    public void Принудительное_завершение_отдаёт_накопленный_звук()
    {
        var settings = new SegmentationSettings { SilenceToEndSegmentMs = 5000, MinSpeechMs = 50 };
        var segmenter = new SpeechSegmenter(new EnergyVoiceActivityDetector(0.5), settings);

        SpeechSegmentEventArgs? completed = null;
        segmenter.SegmentCompleted += (_, args) => completed = args;

        segmenter.Push(Silence(300));
        segmenter.Push(Tone(500));
        segmenter.ForceComplete(SegmentEndReason.Escape);

        Assert.NotNull(completed);
        Assert.Equal(SegmentEndReason.Escape, completed!.Reason);
        Assert.False(segmenter.IsInSpeech);
    }

    [Fact]
    public void Конвертер_приводит_стерео_48к_к_моно_16к()
    {
        const int sourceRate = 48000;
        const int channels = 2;
        var frames = sourceRate / 10; // 100 мс
        var input = new byte[frames * channels * sizeof(float)];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)Math.Sin(2 * Math.PI * 440 * i / sourceRate) * 0.5f;
            BitConverter.TryWriteBytes(input.AsSpan((i * channels) * sizeof(float), sizeof(float)), value);
            BitConverter.TryWriteBytes(input.AsSpan((i * channels + 1) * sizeof(float), sizeof(float)), value);
        }

        var output = AudioFormatConverter.FromFloat32(input, sourceRate, channels);

        Assert.Equal(AudioFormat.BytesFromMilliseconds(100), output.Length);
        Assert.True(AudioFormatConverter.ComputeLevelDb(output) > -20);
    }

    [Fact]
    public void Уровень_тишины_около_нижней_границы()
    {
        Assert.Equal(-96.0, AudioFormatConverter.ComputeLevelDb(Silence(100)));
    }
}
