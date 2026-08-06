using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.Core.Abstractions;

public sealed class PartialResultEventArgs : EventArgs
{
    public PartialResultEventArgs(string text) => Text = text;

    public string Text { get; }
}

/// <summary>
/// Потоковый распознаватель (sherpa-onnx, Zipformer): принимает аудио кусками и отдаёт
/// промежуточные гипотезы с задержкой в сотни миллисекунд.
/// </summary>
public interface IStreamingRecognizer : IDisposable
{
    bool IsReady { get; }

    RecognitionLanguage CurrentLanguage { get; }

    event EventHandler<PartialResultEventArgs>? PartialResult;

    /// <summary>Распознаватель сам решил, что фраза закончилась, и отдал финальный текст.</summary>
    event EventHandler<PartialResultEventArgs>? FinalResult;

    /// <summary>Загружает модель нужного языка. Между сегментами вызывается заново только при смене языка.</summary>
    Task PrepareAsync(RecognitionLanguage language, CancellationToken cancellationToken);

    /// <summary>Сбрасывает состояние перед новым сегментом.</summary>
    void ResetSegment();

    /// <summary>Передаёт очередной кусок PCM.</summary>
    void AcceptAudio(ReadOnlySpan<byte> pcm);

    /// <summary>Забирает финальный результат текущего сегмента.</summary>
    string FlushFinalResult();
}

/// <summary>Запрос на финальное распознавание одного завершённого сегмента.</summary>
/// <param name="SegmentId">Нужен, чтобы результат применился ровно к своему сегменту.</param>
/// <param name="Pcm">Тот же аудиобуфер, который слышал потоковый распознаватель.</param>
/// <param name="Language">Язык сегмента; Auto означает автоопределение Whisper.</param>
/// <param name="PreviousContext">Короткий текстовый контекст предыдущего сегмента или null.</param>
public sealed record FinalRecognitionRequest(long SegmentId, byte[] Pcm, RecognitionLanguage Language, string? PreviousContext);

public sealed record FinalRecognitionResult(long SegmentId, string Text, TimeSpan Duration, bool Succeeded, string? Error = null)
{
    public static FinalRecognitionResult Failure(long segmentId, string error, TimeSpan duration) =>
        new(segmentId, string.Empty, duration, false, error);
}

/// <summary>
/// Финальный распознаватель (whisper.cpp). Модель загружается один раз и
/// живёт всё время работы приложения: процесс на фразу не запускается.
/// </summary>
public interface IFinalRecognizer : IDisposable
{
    bool IsReady { get; }

    Task PrepareAsync(CancellationToken cancellationToken);

    Task<FinalRecognitionResult> TranscribeAsync(FinalRecognitionRequest request, CancellationToken cancellationToken);
}
