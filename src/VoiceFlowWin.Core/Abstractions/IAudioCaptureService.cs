namespace VoiceFlowWin.Core.Abstractions;

/// <summary>Формат, в котором работает весь конвейер: PCM 16 kHz, mono, 16 bit.</summary>
public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSample = BitsPerSample / 8;

    public static int BytesPerMillisecond => SampleRate * Channels * BytesPerSample / 1000;

    public static int MillisecondsFromBytes(int byteCount) => byteCount / BytesPerMillisecond;

    public static int BytesFromMilliseconds(int milliseconds) => milliseconds * BytesPerMillisecond;
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public sealed class AudioFrameEventArgs : EventArgs
{
    public AudioFrameEventArgs(byte[] pcm, int length, double levelDb)
    {
        Pcm = pcm;
        Length = length;
        LevelDb = levelDb;
    }

    /// <summary>PCM 16 kHz mono 16 bit. Буфер принадлежит подписчику только на время вызова.</summary>
    public byte[] Pcm { get; }

    public int Length { get; }

    /// <summary>Уровень сигнала для индикатора overlay.</summary>
    public double LevelDb { get; }

    public ReadOnlySpan<byte> Span => Pcm.AsSpan(0, Length);
}

/// <summary>Захват микрофона. Реализация живёт в VoiceFlowWin.Windows (WASAPI через NAudio).</summary>
public interface IAudioCaptureService : IDisposable
{
    bool IsCapturing { get; }

    event EventHandler<AudioFrameEventArgs>? FrameCaptured;

    /// <summary>Захват прервался сам: устройство отключили или сменили.</summary>
    event EventHandler<string>? CaptureFailed;

    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    /// <summary>Запускает захват. Повторный вызов при активном захвате безопасен.</summary>
    void Start(string? deviceId, double gainDb);

    void Stop();
}
