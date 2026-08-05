using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Abstractions;

public enum SegmentEndReason
{
    /// <summary>Пауза достаточной длины.</summary>
    Silence,

    /// <summary>Достигнута максимальная длительность сегмента.</summary>
    MaxDuration,

    /// <summary>Пользователь остановил диктовку клавишей.</summary>
    Manual,

    /// <summary>Нажат Esc.</summary>
    Escape,
}

public sealed class SpeechSegmentEventArgs : EventArgs
{
    public SpeechSegmentEventArgs(byte[] pcm, SegmentEndReason reason)
    {
        Pcm = pcm;
        Reason = reason;
    }

    public byte[] Pcm { get; }

    public SegmentEndReason Reason { get; }

    public int DurationMilliseconds => AudioFormat.MillisecondsFromBytes(Pcm.Length);
}

/// <summary>Уровень сигнала и решение VAD по очередному кадру.</summary>
public readonly record struct AudioLevelInfo(bool IsSpeech, double Rms, double LevelDb);

/// <summary>Режет поток микрофона на речевые сегменты.</summary>
public interface ISpeechSegmenter
{
    bool IsInSpeech { get; }

    int CurrentSegmentMilliseconds { get; }

    event EventHandler? SpeechStarted;

    event EventHandler<SpeechSegmentEventArgs>? SegmentCompleted;

    event EventHandler<AudioLevelInfo>? LevelChanged;

    void Push(ReadOnlySpan<byte> pcm);

    /// <summary>Закрывает сегмент по команде пользователя, а не по паузе.</summary>
    void ForceComplete(SegmentEndReason reason);

    void UpdateSettings(SegmentationSettings settings);

    void Reset();
}

/// <summary>Очередь финальных распознаваний с ограниченным параллелизмом.</summary>
public interface IFinalRecognitionQueue
{
    int PendingCount { get; }

    event EventHandler<FinalRecognitionResult>? ResultReady;

    bool Enqueue(FinalRecognitionRequest request);
}
