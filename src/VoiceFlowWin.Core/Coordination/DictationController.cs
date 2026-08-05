using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
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
/// Сводит воедино микрофон, VAD, Vosk, Whisper и координатор текста.
/// </summary>
/// <remarks>
/// Все события приходят из разных потоков: аудио — из потока WASAPI, гипотезы
/// Vosk — из потока распознавания, результаты Whisper — из очереди, нажатия
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
    private readonly ILogger<DictationController> _logger;

    private readonly Channel<Func<CancellationToken, Task>> _work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private long? _currentSegmentId;

    /// <summary>Окно, в котором началась текущая диктовка.</summary>
    private WindowFocusSnapshot _sessionFocus = WindowFocusSnapshot.Unknown;
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

        _coordinator.EndSession();
        SetState(DictationState.Idle);
    }

    private void OnFrameCaptured(object? sender, AudioFrameEventArgs e)
    {
        if (!IsDictating)
        {
            return;
        }

        try
        {
            // И VAD, и Vosk получают ровно один и тот же звук.
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

        var voskFinal = _streaming.FlushFinalResult();
        await _coordinator.EndSegmentAsync(segmentId, voskFinal, e.Pcm, token).ConfigureAwait(false);

        var segment = _coordinator.FindSegment(segmentId);
        if (segment is not null && e.Pcm.Length > 0)
        {
            var language = _settings.Current.General.LanguageMode == LanguageSelectionMode.WhisperAutoDetect
                ? RecognitionLanguage.Auto
                : segment.Language;

            _finalQueue.Enqueue(new FinalRecognitionRequest(
                segmentId,
                e.Pcm,
                language,
                _coordinator.BuildWhisperContext()));
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
        State = state;
        StateChanged?.Invoke(this, new DictationStateEventArgs(state, message));
    }
}
