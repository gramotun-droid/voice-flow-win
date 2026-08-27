using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.History;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Coordination;

public enum DictationState
{
    Idle,
    Preparing,
    Listening,
    Speaking,
    Finalizing,
    Error,
}

public sealed class DictationStateEventArgs : EventArgs
{
    public DictationStateEventArgs(DictationState state, string? message = null)
    {
        State = state;
        Message = message;
    }

    public DictationState State { get; }

    public string? Message { get; }
}

/// <summary>
/// Сводит воедино микрофон, VAD, Zipformer, Whisper и координатор текста.
/// </summary>
/// <remarks>
/// Все события приходят из разных потоков: аудио — из потока WASAPI, гипотезы
/// потоковая модель — из потока распознавания, результаты Whisper — из очереди, нажатия
/// клавиш — из потока хука. Работать с состоянием сегментов из всех этих
/// потоков одновременно нельзя, поэтому события не выполняются на месте, а
/// складываются в канал и обрабатываются строго по одному в единственном
/// фоновом потоке. Это же гарантирует порядок: сегмент не может завершиться
/// раньше, чем к нему применилась последняя гипотеза.
/// </remarks>
public sealed class DictationController : IAsyncDisposable
{
    private readonly IAudioCaptureService _capture;
    private readonly IStreamingRecognizer _streaming;
    private readonly ISpeechSegmenter _segmenter;
    private readonly IFinalRecognitionQueue _finalQueue;
    private readonly HybridTranscriptionCoordinator _coordinator;
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IEscapeStopService _escape;
    private readonly IFocusTracker _focusTracker;
    private readonly IInputInterventionMonitor _intervention;
    private readonly ISettingsService _settings;
    private readonly IDictationHistoryStore _history;
    private readonly ILogger<DictationController> _logger;

    private readonly Channel<Func<CancellationToken, Task>> _work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    /// <summary>Сколько ждать результата прохода по всей диктовке.</summary>
    private static readonly TimeSpan FullPassTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Сколько ждать, пока применятся результаты уже отправленных фраз.</summary>
    private static readonly TimeSpan PendingResultsTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PendingResultsPollInterval = TimeSpan.FromMilliseconds(100);

    private long? _currentSegmentId;

    /// <summary>Окно, в котором началась текущая диктовка.</summary>
    private WindowFocusSnapshot _sessionFocus = WindowFocusSnapshot.Unknown;
    private DateTimeOffset _sessionStartedAt;
    private bool _disposed;

    public DictationController(
        IAudioCaptureService capture,
        IStreamingRecognizer streaming,
        ISpeechSegmenter segmenter,
        IFinalRecognitionQueue finalQueue,
        HybridTranscriptionCoordinator coordinator,
        IGlobalHotkeyService hotkeys,
        IEscapeStopService escape,
        IFocusTracker focusTracker,
        IInputInterventionMonitor intervention,
        ISettingsService settings,
        IDictationHistoryStore history,
        ILogger<DictationController>? logger = null)
    {
        _capture = capture;
        _streaming = streaming;
        _segmenter = segmenter;
        _finalQueue = finalQueue;
        _coordinator = coordinator;
        _hotkeys = hotkeys;
        _escape = escape;
        _focusTracker = focusTracker;
        _intervention = intervention;
        _settings = settings;
        _history = history;
        _logger = logger ?? NullLogger<DictationController>.Instance;

        _capture.FrameCaptured += OnFrameCaptured;
        _capture.CaptureFailed += OnCaptureFailed;
        _segmenter.SpeechStarted += OnSpeechStarted;
        _segmenter.SegmentCompleted += OnSegmentCompleted;
        _segmenter.LevelChanged += (_, level) => LevelChanged?.Invoke(this, level);
        _streaming.PartialResult += OnPartialResult;
        _finalQueue.ResultReady += OnFinalResultReady;
        _hotkeys.HotkeyTriggered += OnHotkeyTriggered;
        _escape.EscapePressed += OnEscapePressed;
        _intervention.InterventionDetected += OnInterventionDetected;

        _pump = Task.Run(RunPumpAsync);
    }

    public bool IsDictating { get; private set; }

    public DictationState State { get; private set; } = DictationState.Idle;

    public event EventHandler<DictationStateEventArgs>? StateChanged;

    public event EventHandler<AudioLevelInfo>? LevelChanged;

    /// <summary>Идёт ли сейчас проход по всей диктовке.</summary>
    /// <remarks>
    /// Пока он идёт, приложение переписывает уже введённый текст, поэтому
    /// интерфейс показывает курсор ожидания: правка пользователем в этот
    /// момент привела бы к отказу от замены и потере результата.
    /// </remarks>
    public event EventHandler<bool>? FullPassRunningChanged;

    /// <summary>Регистрирует горячую клавишу. Вызывается при старте и после изменения настроек.</summary>
    public bool ApplyHotkeySettings()
    {
        var general = _settings.Current.General;
        return _hotkeys.Register(general.Hotkey, general.ActivationMode);
    }

    /// <summary>Включает или выключает диктовку — реакция на основную горячую клавишу в режиме Toggle.</summary>
    public void Toggle()
    {
        if (IsDictating)
        {
            Post(token => StopInternalAsync(SegmentEndReason.Manual, cancelSegment: false, token));
        }
        else
        {
            Post(StartInternalAsync);
        }
    }

    public void Start() => Post(StartInternalAsync);

    public void Stop(SegmentEndReason reason = SegmentEndReason.Manual) =>
        Post(token => StopInternalAsync(reason, cancelSegment: false, token));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.CaptureFailed -= OnCaptureFailed;
        _segmenter.SpeechStarted -= OnSpeechStarted;
        _segmenter.SegmentCompleted -= OnSegmentCompleted;
        _streaming.PartialResult -= OnPartialResult;
        _finalQueue.ResultReady -= OnFinalResultReady;
        _hotkeys.HotkeyTriggered -= OnHotkeyTriggered;
        _escape.EscapePressed -= OnEscapePressed;
        _intervention.InterventionDetected -= OnInterventionDetected;

        _capture.Stop();
        _work.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение.
        }

        _shutdown.Dispose();
    }

    /// <summary>Язык сегмента по настройкам и раскладке активного окна.</summary>
    internal RecognitionLanguage ResolveLanguage(WindowFocusSnapshot focus) => _settings.Current.General.LanguageMode switch
    {
        LanguageSelectionMode.AlwaysRussian => RecognitionLanguage.Russian,
        LanguageSelectionMode.AlwaysEnglish => RecognitionLanguage.English,
        LanguageSelectionMode.WhisperAutoDetect => RecognitionLanguage.Auto,
        _ => focus.LayoutLanguage,
    };

    private async Task StartInternalAsync(CancellationToken cancellationToken)
    {
        if (IsDictating)
        {
            return;
        }

        SetState(DictationState.Preparing);

        try
        {
            var focus = CaptureTargetFocus();
            _sessionFocus = focus;
            _sessionStartedAt = DateTimeOffset.UtcNow;
            var language = ResolveLanguage(focus);
            await _streaming.PrepareAsync(language, cancellationToken).ConfigureAwait(false);

            _segmenter.UpdateSettings(_settings.Current.Segmentation);
            _segmenter.Reset();
            _streaming.ResetSegment();

            var microphone = _settings.Current.Microphone;
            _capture.Start(string.IsNullOrWhiteSpace(microphone.DeviceId) ? null : microphone.DeviceId, microphone.GainDb);

            // Esc обязан работать всегда, пока идёт диктовка.
            _escape.Arm(_settings.Current.General.SuppressEscapeDuringDictation);

            IsDictating = true;
            SetState(DictationState.Listening);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось начать диктовку.");
            _capture.Stop();
            _escape.Disarm();
            IsDictating = false;
            SetState(DictationState.Error, ex.Message);
        }
    }

    private async Task StopInternalAsync(SegmentEndReason reason, bool cancelSegment, CancellationToken cancellationToken)
    {
        if (!IsDictating)
        {
            return;
        }

        IsDictating = false;
        SetState(DictationState.Finalizing);

        _capture.Stop();
        _escape.Disarm();

        if (cancelSegment && _currentSegmentId is { } cancelId)
        {
            await _coordinator.CancelSegmentAsync(cancelId, cancellationToken).ConfigureAwait(false);
            _currentSegmentId = null;
            _segmenter.Reset();
            _streaming.ResetSegment();
            _coordinator.EndSession();
            SetState(DictationState.Idle);
            return;
        }

        // Незавершённый сегмент нужно закрыть: иначе последняя фраза
        // осталась бы без финальной обработки Whisper.
        _segmenter.ForceComplete(reason);
        await Task.Yield();

        await WaitForPendingResultsAsync(cancellationToken).ConfigureAwait(false);
        var sessionAudio = _coordinator.BuildSessionAudio();
        await RunFullPassAsync(sessionAudio, cancellationToken).ConfigureAwait(false);
        await SaveHistoryAsync(sessionAudio, cancellationToken).ConfigureAwait(false);

        _coordinator.EndSession();
        SetState(DictationState.Idle);
    }

    /// <summary>Прогоняет всю диктовку целиком и заменяет введённый текст.</summary>
    /// <remarks>
    /// Отдельные фразы уже исправлены, но между собой они не согласованы:
    /// Whisper видел каждую по отдельности. Проход по всей записи видит их
    /// вместе — отсюда общая пунктуация и формы слов. Любая ошибка прохода
    /// оставляет пофразный результат нетронутым: он уже в поле.
    /// </remarks>
    private async Task RunFullPassAsync(byte[] audio, CancellationToken cancellationToken)
    {
        if (!_settings.Current.Whisper.FullPassAfterStop)
        {
            _logger.LogInformation("Проход по всей диктовке выключен в настройках.");
            return;
        }

        var injected = _coordinator.SessionInjectedText;
        if (audio.Length == 0 || injected.Length == 0)
        {
            _logger.LogInformation(
                "Проход по всей диктовке пропущен: звука {AudioBytes} Б, введено {InjectedLength} символов.",
                audio.Length,
                injected.Length);
            return;
        }

        FullPassRunningChanged?.Invoke(this, true);
        var started = DateTimeOffset.UtcNow;

        try
        {
            var language = ResolveLanguage(_sessionFocus);
            _logger.LogInformation(
                "Проход по всей диктовке начат: {Seconds:0.0} с звука, язык {Language}, в поле {InjectedLength} символов.",
                audio.Length / (double)(AudioFormat.SampleRate * AudioFormat.BytesPerSample),
                language,
                injected.Length);

            // Время прохода ограничено: Whisper на длинной записи может считать
            // минутами, а всё это время пользователь видит «Финализация» и
            // курсор ожидания. Лучше отказаться от прохода, чем висеть.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FullPassTimeout);

            var request = new FinalRecognitionRequest(
                SegmentId: 0,
                Pcm: audio,
                Language: language,
                PreviousContext: null);

            var result = await _finalQueue.TranscribeNowAsync(request, timeout.Token).ConfigureAwait(false);
            if (!result.Succeeded || result.Text.Length == 0)
            {
                _logger.LogWarning(
                    "Проход по всей диктовке не дал результата за {Elapsed}: {Error}",
                    DateTimeOffset.UtcNow - started,
                    result.Error ?? "пустой текст");
                return;
            }

            _logger.LogInformation(
                "Проход по всей диктовке распознал за {Elapsed}: {TextLength} символов.",
                DateTimeOffset.UtcNow - started,
                result.Text.Length);

            var applied = await _coordinator.ApplySessionCorrectionAsync(result.Text, timeout.Token).ConfigureAwait(false);
            _logger.LogInformation(
                applied
                    ? "Текст диктовки заменён результатом полного прохода."
                    : "Замена по результату полного прохода не выполнена.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Проход по всей диктовке прерван по таймауту {Timeout}. В поле остался пофразный результат.",
                FullPassTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Пофразный результат уже в поле — отказ прохода ничего не портит.
            _logger.LogWarning(ex, "Проход по всей диктовке не выполнен.");
        }
        finally
        {
            FullPassRunningChanged?.Invoke(this, false);
        }
    }

    private async Task SaveHistoryAsync(byte[] audio, CancellationToken cancellationToken)
    {
        var text = _coordinator.SessionInjectedText;
        if (text.Length == 0 && audio.Length == 0)
        {
            return;
        }

        try
        {
            await _history.SaveSessionAsync(
                new DictationSessionSnapshot(
                    _sessionStartedAt,
                    DateTimeOffset.UtcNow,
                    ResolveLanguage(_sessionFocus),
                    text,
                    audio),
                _settings.Current.Privacy,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // История — опциональная функция: ошибка диска не должна оставлять
            // диктовку в состоянии Finalizing после успешной вставки текста.
            _logger.LogWarning(ex, "Сеанс диктовки не удалось сохранить в локальную историю.");
        }
    }

    /// <summary>Ждёт, пока применятся результаты уже отправленных фраз.</summary>
    /// <remarks>
    /// Полный проход заменяет весь введённый текст, поэтому он обязан начаться
    /// после того, как последние фразы окажутся в поле. Иначе запоздавший
    /// результат фразы допишется поверх уже заменённого текста.
    /// </remarks>
    private async Task WaitForPendingResultsAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PendingResultsTimeout;

        while (_finalQueue.PendingCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PendingResultsPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (_finalQueue.PendingCount > 0)
        {
            _logger.LogWarning(
                "Ожидание последних фраз прервано по таймауту: в очереди осталось {Pending}.",
                _finalQueue.PendingCount);
        }
    }

    private void OnFrameCaptured(object? sender, AudioFrameEventArgs e)
    {
        if (!IsDictating)
        {
            return;
        }

        try
        {
            // И VAD, и потоковая модель получают ровно один и тот же звук.
            _segmenter.Push(e.Span);
            _streaming.AcceptAudio(e.Span);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки аудиокадра.");
        }
    }

    private void OnCaptureFailed(object? sender, string message)
    {
        _logger.LogWarning("Захват звука прерван: {Message}", message);
        Post(token => StopInternalAsync(SegmentEndReason.Manual, cancelSegment: false, token));
        SetState(DictationState.Error, message);
    }

    private void OnSpeechStarted(object? sender, EventArgs e) => Post(_ =>
    {
        EnsureSegment();
        SetState(DictationState.Speaking);
        return Task.CompletedTask;
    });

    private void OnPartialResult(object? sender, PartialResultEventArgs e) => Post(async token =>
    {
        var segmentId = EnsureSegment();
        await _coordinator.OnPartialResultAsync(segmentId, e.Text, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
    });

    private void OnSegmentCompleted(object? sender, SpeechSegmentEventArgs e) => Post(async token =>
    {
        if (_currentSegmentId is not { } segmentId)
        {
            return;
        }

        var streamingFinal = _streaming.FlushFinalResult();
        _logger.LogInformation(
            "Фраза {SegmentId} закрыта ({Reason}): потоковый результат {TextLength} символов, звука {Seconds:0.0} с.",
            segmentId,
            e.Reason,
            streamingFinal.Length,
            e.Pcm.Length / (double)(AudioFormat.SampleRate * AudioFormat.BytesPerSample));

        await _coordinator.EndSegmentAsync(segmentId, streamingFinal, e.Pcm, token).ConfigureAwait(false);

        var segment = _coordinator.FindSegment(segmentId);
        if (segment is not null && e.Pcm.Length > 0)
        {
            var language = _settings.Current.General.LanguageMode == LanguageSelectionMode.WhisperAutoDetect
                ? RecognitionLanguage.Auto
                : segment.Language;

            var context = _coordinator.BuildWhisperContext();
            var accepted = _finalQueue.Enqueue(new FinalRecognitionRequest(segmentId, e.Pcm, language, context));

            _logger.LogInformation(
                "Фраза {SegmentId} отправлена в Whisper: язык {Language}, контекст {ContextLength} символов, принята: {Accepted}, в очереди {Pending}.",
                segmentId,
                language,
                context?.Length ?? 0,
                accepted,
                _finalQueue.PendingCount);
        }

        _currentSegmentId = null;
        _streaming.ResetSegment();

        if (IsDictating)
        {
            SetState(DictationState.Listening);
        }
    });

    private void OnFinalResultReady(object? sender, FinalRecognitionResult result) => Post(async token =>
    {
        _logger.LogInformation(
            "Whisper вернул для фразы {SegmentId} за {Duration}: успех {Succeeded}, текст {TextLength} символов{Error}",
            result.SegmentId,
            result.Duration,
            result.Succeeded,
            result.Text.Length,
            result.Error is null ? string.Empty : ", ошибка: " + result.Error);

        await _coordinator.ApplyFinalRecognitionAsync(result, token).ConfigureAwait(false);
    });

    private void OnHotkeyTriggered(object? sender, HotkeyEventArgs e)
    {
        var mode = _settings.Current.General.ActivationMode;

        if (mode == DictationActivationMode.Toggle)
        {
            if (e.IsPressed)
            {
                Toggle();
            }

            return;
        }

        if (e.IsPressed)
        {
            Post(StartInternalAsync);
        }
        else
        {
            Post(token => StopInternalAsync(SegmentEndReason.Manual, cancelSegment: false, token));
        }
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        // Esc останавливает диктовку в любом режиме. Сохранять ли уже
        // распознанное — решает пользователь в настройках.
        var cancel = _settings.Current.General.EscapeBehavior == EscapeBehavior.CancelSegment;
        Post(token => StopInternalAsync(SegmentEndReason.Escape, cancel, token));
    }

    private void OnInterventionDetected(object? sender, InterventionEventArgs e) => Post(_ =>
    {
        _coordinator.NotifyIntervention(e.Kind, e.Description);
        return Task.CompletedTask;
    });

    private long EnsureSegment()
    {
        if (_currentSegmentId is { } existing)
        {
            return existing;
        }

        var focus = CaptureTargetFocus();
        var segment = _coordinator.BeginSegment(ResolveLanguage(focus), focus);
        _currentSegmentId = segment.SegmentId;
        return segment.SegmentId;
    }

    /// <summary>Снимок окна, в которое идёт диктовка.</summary>
    /// <remarks>
    /// Собственные окна приложения целью диктовки быть не могут. Проверка не
    /// теоретическая: overlay показывается ровно в момент начала диктовки, и
    /// когда снимок попадал на него, язык сегмента определялся по раскладке
    /// нашего же окна — русская речь уходила в Whisper как английская. По той
    /// же причине от него нельзя отсчитывать вмешательство пользователя.
    /// </remarks>
    internal WindowFocusSnapshot CaptureTargetFocus()
    {
        var focus = _focusTracker.Capture();

        var isOwnWindow = focus.WindowHandle != 0 && focus.ProcessId == Environment.ProcessId;
        if ((isOwnWindow || focus.WindowHandle == 0) && _sessionFocus.WindowHandle != 0)
        {
            return _sessionFocus;
        }

        return focus;
    }

    private void Post(Func<CancellationToken, Task> work)
    {
        if (_disposed)
        {
            return;
        }

        _work.Writer.TryWrite(work);
    }

    private async Task RunPumpAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await work(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Одна сбойная операция не должна останавливать диктовку.
                    _logger.LogError(ex, "Ошибка обработки события диктовки.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Выход из приложения.
        }
    }

    private void SetState(DictationState state, string? message = null)
    {
        _logger.LogInformation("Состояние: {State}{Message}", state, message is null ? string.Empty : " — " + message);
        State = state;
        StateChanged?.Invoke(this, new DictationStateEventArgs(state, message));
    }
}
