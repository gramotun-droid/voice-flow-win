using VoiceFlowWin.Core.Text;

namespace VoiceFlowWin.Core.Models;

/// <summary>
/// Всё, что известно об одном речевом сегменте: исходное аудио, потоковые гипотезы,
/// реально вставленный в чужое приложение текст и результат Whisper.
/// </summary>
/// <remarks>
/// Ключевое поле — <see cref="InjectedText"/>. Приложение никогда не ищет свою
/// фразу по документу строковым поиском: оно точно знает, сколько символов
/// вставило само, и заменяет только их.
/// </remarks>
public sealed class DictationSegment
{
    private readonly List<string> _hypotheses = new();

    public DictationSegment(long segmentId, RecognitionLanguage language, WindowFocusSnapshot focus, DateTimeOffset startedAt)
    {
        SegmentId = segmentId;
        Language = language;
        StartFocus = focus;
        StartedAt = startedAt;
        State = SegmentState.Recording;
    }

    public long SegmentId { get; }

    /// <summary>Язык, выбранный в момент начала сегмента; смена раскладки его не меняет.</summary>
    public RecognitionLanguage Language { get; }

    /// <summary>Раскладка и окно на момент начала — база для контроля вмешательства.</summary>
    public WindowFocusSnapshot StartFocus { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; private set; }

    public SegmentState State { get; private set; }

    /// <summary>Исходный PCM 16 kHz mono 16 bit, который получают и Zipformer, и Whisper.</summary>
    public byte[] Audio { get; private set; } = Array.Empty<byte>();

    /// <summary>Последняя промежуточная гипотеза целиком.</summary>
    public string PartialText { get; private set; } = string.Empty;

    /// <summary>Стабильная часть потоковой гипотезы — то, что разрешено вводить.</summary>
    public string StableText { get; private set; } = string.Empty;

    /// <summary>Изменяемый хвост: показывается в overlay, в поле попадает только в «живом» режиме.</summary>
    public string VolatileTail { get; private set; } = string.Empty;

    /// <summary>Текст, который приложение действительно отправило в активное окно.</summary>
    public string InjectedText { get; private set; } = string.Empty;

    /// <summary>Длина <see cref="InjectedText"/> в текстовых элементах, а не в char.</summary>
    public int InjectedLength => TextElements.Count(InjectedText);

    /// <summary>Финальный текст Whisper после нормализации и словаря.</summary>
    public string? WhisperText { get; private set; }

    /// <summary>Пользователь трогал текст или переключал окно после начала сегмента.</summary>
    public bool UserIntervened { get; private set; }

    public string? FailureReason { get; private set; }

    public IReadOnlyList<string> Hypotheses => _hypotheses;

    /// <summary>Текст, который сейчас считается результатом сегмента.</summary>
    public string EffectiveText => WhisperText ?? InjectedText;

    public void AppendAudio(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        var combined = new byte[Audio.Length + pcm.Length];
        Audio.CopyTo(combined, 0);
        pcm.CopyTo(combined.AsSpan(Audio.Length));
        Audio = combined;
    }

    public void SetAudio(byte[] pcm) => Audio = pcm;

    public void RecordHypothesis(string partial, string stable, string tail)
    {
        PartialText = partial;
        StableText = stable;
        VolatileTail = tail;
        _hypotheses.Add(partial);

        if (State == SegmentState.Recording && partial.Length > 0)
        {
            State = SegmentState.Streaming;
        }
    }

    /// <summary>Фиксирует факт вставки текста в активное приложение.</summary>
    public void RecordInjection(string newInjectedText) => InjectedText = newInjectedText;

    public void MarkUserIntervention(string reason)
    {
        UserIntervened = true;
        FailureReason ??= reason;
        if (!State.IsTerminal())
        {
            State = SegmentState.Frozen;
        }
    }

    public void MarkAwaitingFinalization(DateTimeOffset endedAt)
    {
        EndedAt = endedAt;
        if (State.IsCapturing())
        {
            State = SegmentState.AwaitingFinalization;
        }
    }

    /// <summary>Закрывает фразу без отдельного корректирующего распознавания.</summary>
    public void MarkStreamingCompleted(DateTimeOffset endedAt)
    {
        EndedAt = endedAt;
        if (!State.IsTerminal())
        {
            State = SegmentState.Finalized;
        }
    }

    public void MarkWhisperProcessing()
    {
        if (State == SegmentState.AwaitingFinalization)
        {
            State = SegmentState.WhisperProcessing;
        }
    }

    public void MarkFinalized(string finalText)
    {
        WhisperText = finalText;
        if (!State.IsTerminal())
        {
            State = SegmentState.Finalized;
        }
    }

    /// <summary>Whisper отработал, но текст применять нельзя — сегмент заморожен пользователем.</summary>
    public void MarkFrozenWithResult(string finalText)
    {
        WhisperText = finalText;
        State = SegmentState.Frozen;
    }

    public void MarkCancelled()
    {
        State = SegmentState.Cancelled;
        EndedAt ??= DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        FailureReason = reason;
        State = SegmentState.Failed;
        EndedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>Очищает аудиобуфер: по умолчанию звук не хранится дольше обработки.</summary>
    public void ReleaseAudio() => Audio = Array.Empty<byte>();
}
