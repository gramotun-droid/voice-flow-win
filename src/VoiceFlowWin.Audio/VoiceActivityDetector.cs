using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.Audio;

/// <summary>Решение детектора по одному кадру.</summary>
public readonly record struct VadFrameResult(bool IsSpeech, double Rms, double NoiseFloor)
{
    public double SignalToNoise => NoiseFloor <= double.Epsilon ? Rms : Rms / NoiseFloor;
}

/// <summary>Локальный детектор речевой активности.</summary>
public interface IVoiceActivityDetector
{
    /// <summary>Длина кадра, который ожидает детектор.</summary>
    int FrameMilliseconds { get; }

    /// <summary>Порог: 0 — реагировать на шёпот, 1 — только на громкую речь.</summary>
    double Sensitivity { get; set; }

    VadFrameResult Process(ReadOnlySpan<byte> pcmFrame);

    void Reset();
}

/// <summary>
/// Энергетический VAD с адаптивным уровнем шума.
/// </summary>
/// <remarks>
/// Работает без внешних моделей и без ONNX Runtime: считает RMS кадра и
/// частоту переходов через ноль, а порог подстраивает под фоновый шум
/// помещения. Гистерезис (порог включения выше порога выключения) не даёт
/// сегменту рваться на паузах между словами. Интерфейс
/// <see cref="IVoiceActivityDetector"/> оставлен отдельно, чтобы позже можно
/// было подставить Silero VAD, не трогая сегментацию.
/// </remarks>
public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private const double MinimumNoiseFloor = 40.0;
    private const double AttackSmoothing = 0.35;
    private const double DecaySmoothing = 0.02;

    private double _noiseFloor = MinimumNoiseFloor;
    private bool _inSpeech;

    public EnergyVoiceActivityDetector(double sensitivity = 0.5, int frameMilliseconds = 20)
    {
        Sensitivity = sensitivity;
        FrameMilliseconds = frameMilliseconds;
    }

    public int FrameMilliseconds { get; }

    public double Sensitivity { get; set; }

    public VadFrameResult Process(ReadOnlySpan<byte> pcmFrame)
    {
        if (pcmFrame.Length < AudioFormat.BytesPerSample)
        {
            return new VadFrameResult(false, 0, _noiseFloor);
        }

        var rms = ComputeRms(pcmFrame);
        var zeroCrossingRate = ComputeZeroCrossingRate(pcmFrame);

        // Порог включения растёт с чувствительностью: 0 → 2× шума, 1 → 8× шума.
        var enterFactor = 2.0 + 6.0 * Math.Clamp(Sensitivity, 0, 1);
        var exitFactor = enterFactor * 0.6;

        var enterThreshold = Math.Max(_noiseFloor * enterFactor, MinimumNoiseFloor * 2);
        var exitThreshold = Math.Max(_noiseFloor * exitFactor, MinimumNoiseFloor);

        // Постоянный тон с очень низкой частотой переходов через ноль — это
        // гул техники, а не речь.
        var looksLikeSpeech = zeroCrossingRate is > 0.01 and < 0.6;

        if (_inSpeech)
        {
            _inSpeech = rms > exitThreshold;
        }
        else
        {
            _inSpeech = rms > enterThreshold && looksLikeSpeech;
        }

        // Уровень шума поднимаем быстро, опускаем медленно и только в тишине:
        // иначе громкая речь «научит» детектор считать её фоном.
        if (!_inSpeech)
        {
            var smoothing = rms > _noiseFloor ? DecaySmoothing : AttackSmoothing;
            _noiseFloor += smoothing * (rms - _noiseFloor);
            _noiseFloor = Math.Max(MinimumNoiseFloor, _noiseFloor);
        }

        return new VadFrameResult(_inSpeech, rms, _noiseFloor);
    }

    public void Reset()
    {
        _noiseFloor = MinimumNoiseFloor;
        _inSpeech = false;
    }

    private static double ComputeRms(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / AudioFormat.BytesPerSample;
        if (sampleCount == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcm.Slice(i * AudioFormat.BytesPerSample, AudioFormat.BytesPerSample));
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / sampleCount);
    }

    private static double ComputeZeroCrossingRate(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / AudioFormat.BytesPerSample;
        if (sampleCount < 2)
        {
            return 0;
        }

        var crossings = 0;
        var previous = BitConverter.ToInt16(pcm[..AudioFormat.BytesPerSample]);
        for (var i = 1; i < sampleCount; i++)
        {
            var current = BitConverter.ToInt16(pcm.Slice(i * AudioFormat.BytesPerSample, AudioFormat.BytesPerSample));
            if ((previous < 0 && current >= 0) || (previous >= 0 && current < 0))
            {
                crossings++;
            }

            previous = current;
        }

        return (double)crossings / (sampleCount - 1);
    }
}
