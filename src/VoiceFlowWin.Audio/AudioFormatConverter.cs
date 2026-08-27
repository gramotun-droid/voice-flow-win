using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.Audio;

/// <summary>
/// Приводит звук устройства к формату конвейера: PCM 16 kHz, mono, 16 bit.
/// </summary>
/// <remarks>
/// WASAPI в shared mode отдаёт то, что настроено в системе: обычно 32-битный
/// float, 44.1 или 48 kHz, стерео. И Zipformer, и whisper.cpp ждут 16 kHz mono.
/// Преобразование сделано вручную, а не средствами NAudio, чтобы его можно
/// было покрыть тестами на любой платформе.
/// </remarks>
public static class AudioFormatConverter
{
    /// <summary>Сводит каналы в моно и передискретизирует 32-битный float в целевой PCM.</summary>
    public static byte[] FromFloat32(ReadOnlySpan<byte> input, int sourceSampleRate, int sourceChannels, double gainDb = 0)
    {
        if (input.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var frameCount = input.Length / (sizeof(float) * sourceChannels);
        var mono = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                var offset = (frame * sourceChannels + channel) * sizeof(float);
                sum += BitConverter.ToSingle(input.Slice(offset, sizeof(float)));
            }

            mono[frame] = sum / sourceChannels;
        }

        return ResampleToPcm16(mono, sourceSampleRate, gainDb);
    }

    /// <summary>То же самое для устройств, отдающих 16-битные целые.</summary>
    public static byte[] FromPcm16(ReadOnlySpan<byte> input, int sourceSampleRate, int sourceChannels, double gainDb = 0)
    {
        if (input.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var frameCount = input.Length / (sizeof(short) * sourceChannels);
        var mono = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < sourceChannels; channel++)
            {
                var offset = (frame * sourceChannels + channel) * sizeof(short);
                sum += BitConverter.ToInt16(input.Slice(offset, sizeof(short))) / 32768f;
            }

            mono[frame] = sum / sourceChannels;
        }

        return ResampleToPcm16(mono, sourceSampleRate, gainDb);
    }

    private static byte[] ResampleToPcm16(float[] mono, int sourceSampleRate, double gainDb)
    {
        var gain = gainDb == 0 ? 1.0 : Math.Pow(10, gainDb / 20.0);

        float[] resampled;
        if (sourceSampleRate == AudioFormat.SampleRate)
        {
            resampled = mono;
        }
        else
        {
            var targetCount = (int)((long)mono.Length * AudioFormat.SampleRate / sourceSampleRate);
            resampled = new float[targetCount];
            var ratio = (double)sourceSampleRate / AudioFormat.SampleRate;

            for (var i = 0; i < targetCount; i++)
            {
                // Линейная интерполяция: для речи в 16 kHz этого достаточно,
                // а полосовой фильтр стоил бы заметного CPU на каждом кадре.
                var sourcePosition = i * ratio;
                var index = (int)sourcePosition;
                var fraction = sourcePosition - index;
                var first = mono[Math.Min(index, mono.Length - 1)];
                var second = mono[Math.Min(index + 1, mono.Length - 1)];
                resampled[i] = (float)(first + (second - first) * fraction);
            }
        }

        var result = new byte[resampled.Length * sizeof(short)];
        for (var i = 0; i < resampled.Length; i++)
        {
            var amplified = resampled[i] * gain;
            var sample = (short)Math.Clamp(amplified * 32767.0, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(result.AsSpan(i * sizeof(short), sizeof(short)), sample);
        }

        return result;
    }

    /// <summary>Уровень сигнала в дБFS для индикатора микрофона.</summary>
    public static double ComputeLevelDb(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / AudioFormat.BytesPerSample;
        if (sampleCount == 0)
        {
            return -96.0;
        }

        double sum = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcm16.Slice(i * AudioFormat.BytesPerSample, AudioFormat.BytesPerSample)) / 32768.0;
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / sampleCount);
        return rms <= 1e-8 ? -96.0 : Math.Max(-96.0, 20 * Math.Log10(rms));
    }
}
