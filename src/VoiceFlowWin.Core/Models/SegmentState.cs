namespace VoiceFlowWin.Core.Models;

/// <summary>Жизненный цикл одного речевого сегмента.</summary>
public enum SegmentState
{
    /// <summary>Идёт запись звука, Vosk ещё не выдал ни одной гипотезы.</summary>
    Recording,

    /// <summary>Vosk выдаёт промежуточные результаты, стабильный префикс вводится в поле.</summary>
    Streaming,

    /// <summary>Речь закончилась, аудио собрано, сегмент ждёт очереди Whisper.</summary>
    AwaitingFinalization,

    /// <summary>Whisper обрабатывает аудиосегмент.</summary>
    WhisperProcessing,

    /// <summary>Финальный текст применён (или подтверждено, что менять нечего).</summary>
    Finalized,

    /// <summary>Пользователь вмешался в текст: автоматическая замена запрещена.</summary>
    Frozen,

    /// <summary>Сегмент отменён пользователем.</summary>
    Cancelled,

    /// <summary>Ошибка распознавания или вставки; уже введённый текст остаётся как есть.</summary>
    Failed,
}

public static class SegmentStateExtensions
{
    /// <summary>Терминальные состояния больше не меняются и не принимают результат Whisper.</summary>
    public static bool IsTerminal(this SegmentState state) => state is SegmentState.Finalized
        or SegmentState.Frozen
        or SegmentState.Cancelled
        or SegmentState.Failed;

    /// <summary>В этих состояниях сегмент ещё может принимать аудио.</summary>
    public static bool IsCapturing(this SegmentState state) => state is SegmentState.Recording or SegmentState.Streaming;
}
