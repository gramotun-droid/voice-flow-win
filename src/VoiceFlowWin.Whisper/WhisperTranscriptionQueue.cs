using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.WhisperEngine;

/// <summary>
/// Очередь финальных распознаваний.
/// </summary>
/// <remarks>
/// Пока Whisper обрабатывает одну фразу, микрофон продолжает писать следующую —
/// это нормально и нужно. А вот запускать два тяжёлых распознавания
/// одновременно нельзя: на слабом CPU это превращает задержку в десятки
/// секунд. Поэтому задачи выполняются строго по очереди, в порядке
/// поступления, в одном фоновом потоке.
/// </remarks>
public sealed class WhisperTranscriptionQueue : IFinalRecognitionQueue, IAsyncDisposable
{
    private readonly IFinalRecognizer _recognizer;
    private readonly ILogger<WhisperTranscriptionQueue> _logger;
    private readonly Channel<FinalRecognitionRequest> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _pendingCount;
    private bool _disposed;

    public WhisperTranscriptionQueue(
        IFinalRecognizer recognizer,
        ILogger<WhisperTranscriptionQueue>? logger = null,
        int capacity = 16)
    {
        _recognizer = recognizer;
        _logger = logger ?? NullLogger<WhisperTranscriptionQueue>.Instance;
        _channel = Channel.CreateBounded<FinalRecognitionRequest>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Если пользователь говорит быстрее, чем Whisper успевает, теряем
            // самый старый сегмент: он всё равно уже не догонит текст.
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _worker = Task.Run(ProcessQueueAsync);
    }

    /// <summary>Сегмент распознан; результат нужно применить к его SegmentId.</summary>
    public event EventHandler<FinalRecognitionResult>? ResultReady;

    /// <inheritdoc />
    public async Task<FinalRecognitionResult> TranscribeNowAsync(FinalRecognitionRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Распознаватель сам сериализует вызовы, поэтому очередь фраз здесь
        // обходится намеренно: проход по всей диктовке идёт вне её порядка.
        await _recognizer.PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await _recognizer.TranscribeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Сколько сегментов ждёт обработки — для индикатора в overlay.</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public bool Enqueue(FinalRecognitionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_channel.Writer.TryWrite(request))
        {
            _logger.LogWarning("Очередь Whisper переполнена, сегмент {SegmentId} отброшен.", request.SegmentId);
            return false;
        }

        Interlocked.Increment(ref _pendingCount);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение при выходе из приложения.
        }

        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    var result = await _recognizer.TranscribeAsync(request, _shutdown.Token).ConfigureAwait(false);
                    ResultReady?.Invoke(this, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Сбой распознавания сегмента {SegmentId}.", request.SegmentId);
                    ResultReady?.Invoke(this, FinalRecognitionResult.Failure(request.SegmentId, ex.Message, TimeSpan.Zero));
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Выход из приложения — очередь просто перестаёт работать.
        }
    }
}
