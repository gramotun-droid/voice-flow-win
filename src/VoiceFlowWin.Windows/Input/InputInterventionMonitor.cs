using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Следит за тем, не вмешался ли пользователь в текст.
/// </summary>
/// <remarks>
/// Приложение имеет право переписывать только собственный текст и только пока
/// он остаётся там, где был вставлен. Признаков вмешательства несколько:
///
/// — пользователь нажал клавишу (клавиатурный хук, свой ввод отфильтрован по
///   метке в dwExtraInfo);
/// — сменилось активное окно или элемент ввода;
/// — переместилась каретка — в том числе от клика мышью;
/// — прозвучали Ctrl+V или Ctrl+Z.
///
/// Позиция каретки опрашивается таймером, а не отслеживается хуком мыши:
/// клик сам по себе ничего не значит (можно кликнуть по заголовку окна), а
/// вот сдвинувшаяся каретка значит всегда. Ожидаемая позиция обновляется
/// после каждой собственной вставки, иначе приложение принимало бы за
/// вмешательство свой же ввод.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InputInterventionMonitor : IInputInterventionMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Сколько после собственного ввода расхождение каретки считается его
    /// последствием, а не вмешательством.
    /// </summary>
    /// <remarks>
    /// Позиция каретки берётся из её экранных координат, то есть меняется от
    /// каждого напечатанного символа. Поле обновляет координаты асинхронно, уже
    /// после возврата из SendInput, поэтому сразу после вставки наблюдаемая
    /// каретка почти всегда «не там». Без этой паузы непрерывная диктовка сама
    /// себе выставляла признак ручной правки.
    /// </remarks>
    private static readonly TimeSpan CaretSettleWindow = TimeSpan.FromMilliseconds(700);

    private readonly LowLevelKeyboardHook _hook;
    private readonly IFocusTracker _focusTracker;
    private readonly ILogger<InputInterventionMonitor> _logger;
    private readonly object _sync = new();
    private readonly TimeProvider _time;

    private global::System.Threading.Timer? _pollTimer;
    private WindowFocusSnapshot _baseline = WindowFocusSnapshot.Unknown;
    private int _expectedCaret = -1;
    private int _candidateCaret = -1;
    private long _lastSelfInputTicks;
    private int _suppressDepth;
    private InterventionKind _detected = InterventionKind.None;
    private bool _disposed;

    public InputInterventionMonitor(
        LowLevelKeyboardHook hook,
        IFocusTracker focusTracker,
        ILogger<InputInterventionMonitor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _hook = hook;
        _focusTracker = focusTracker;
        _logger = logger ?? NullLogger<InputInterventionMonitor>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _hook.KeyEvent += OnKeyEvent;
    }

    public bool IsWatching { get; private set; }

    public event EventHandler<InterventionEventArgs>? InterventionDetected;

    public void StartWatching(WindowFocusSnapshot baseline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            _baseline = baseline;
            _expectedCaret = baseline.CaretPosition;
            _detected = InterventionKind.None;
            IsWatching = true;

            _hook.Start();
            _pollTimer ??= new global::System.Threading.Timer(_ => Poll(), null, PollInterval, PollInterval);
        }
    }

    public void StopWatching()
    {
        lock (_sync)
        {
            IsWatching = false;
            _detected = InterventionKind.None;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    public IDisposable SuppressSelfInput()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SelfInputScope(this);
    }

    public InterventionKind CheckNow(WindowFocusSnapshot baseline)
    {
        if (!IsWatching)
        {
            return InterventionKind.None;
        }

        lock (_sync)
        {
            if (_detected != InterventionKind.None)
            {
                return _detected;
            }
        }

        var current = _focusTracker.Capture();

        // Пустой снимок означает, что состояние узнать не удалось: система
        // отдаёт его и при обычной перерисовке окна. Это не вмешательство.
        if (current.WindowHandle == 0)
        {
            return InterventionKind.None;
        }

        if (current.WindowHandle != baseline.WindowHandle || current.ProcessId != baseline.ProcessId)
        {
            return Record(InterventionKind.WindowChanged);
        }

        if (current.FocusedControlHandle != 0 &&
            baseline.FocusedControlHandle != 0 &&
            current.FocusedControlHandle != baseline.FocusedControlHandle)
        {
            return Record(InterventionKind.FocusedControlChanged);
        }

        lock (_sync)
        {
            if (_suppressDepth > 0)
            {
                // Пока приложение печатает само, каретка обязана двигаться.
                _candidateCaret = -1;
                return InterventionKind.None;
            }

            if (_expectedCaret < 0 || current.CaretPosition < 0)
            {
                return InterventionKind.None;
            }

            if (current.CaretPosition == _expectedCaret)
            {
                _candidateCaret = -1;
                return InterventionKind.None;
            }

            // Каретка ещё догоняет собственный ввод — принимаем её новое
            // положение за ожидаемое.
            if (_time.GetElapsedTime(_lastSelfInputTicks) < CaretSettleWindow)
            {
                _expectedCaret = current.CaretPosition;
                _candidateCaret = -1;
                return InterventionKind.None;
            }

            // Единичное расхождение может быть промежуточным состоянием
            // отрисовки, поэтому вмешательством считается только устойчивое:
            // одна и та же новая позиция два опроса подряд.
            if (_candidateCaret != current.CaretPosition)
            {
                _candidateCaret = current.CaretPosition;
                return InterventionKind.None;
            }

            return Record(InterventionKind.CaretMoved);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hook.KeyEvent -= OnKeyEvent;
        StopWatching();
    }

    /// <summary>Запоминает позицию каретки после собственной вставки.</summary>
    private void RefreshExpectedCaret()
    {
        var current = _focusTracker.Capture();
        lock (_sync)
        {
            _expectedCaret = current.CaretPosition;
            _candidateCaret = -1;
            _lastSelfInputTicks = _time.GetTimestamp();
        }
    }

    private void Poll()
    {
        if (!IsWatching || Volatile.Read(ref _suppressDepth) > 0)
        {
            return;
        }

        try
        {
            var kind = CheckNow(_baseline);
            if (kind != InterventionKind.None)
            {
                InterventionDetected?.Invoke(this, new InterventionEventArgs(kind, Describe(kind)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка опроса состояния фокуса.");
        }
    }

    private void OnKeyEvent(object? sender, LowLevelKeyEventArgs e)
    {
        if (!IsWatching || !e.IsKeyDown || e.IsInjected || Volatile.Read(ref _suppressDepth) > 0)
        {
            return;
        }

        // Модификаторы сами по себе ничего не меняют в тексте.
        if (e.VirtualKey is NativeMethods.VK_CONTROL or NativeMethods.VK_MENU or NativeMethods.VK_SHIFT
            or NativeMethods.VK_LWIN or NativeMethods.VK_ESCAPE)
        {
            return;
        }

        var control = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        var kind = control switch
        {
            true when e.VirtualKey == NativeMethods.VK_V => InterventionKind.ClipboardPaste,
            true when e.VirtualKey == NativeMethods.VK_Z => InterventionKind.Undo,
            _ => InterventionKind.UserTyped,
        };

        Record(kind);
        InterventionDetected?.Invoke(this, new InterventionEventArgs(kind, Describe(kind)));
    }

    private InterventionKind Record(InterventionKind kind)
    {
        lock (_sync)
        {
            if (_detected == InterventionKind.None)
            {
                _detected = kind;
                _logger.LogInformation("Обнаружено вмешательство пользователя: {Kind}", kind);
            }

            return _detected;
        }
    }

    private static string Describe(InterventionKind kind) => kind switch
    {
        InterventionKind.WindowChanged => "Сменилось активное окно.",
        InterventionKind.FocusedControlChanged => "Сменился элемент ввода.",
        InterventionKind.CaretMoved => "Курсор перемещён.",
        InterventionKind.UserTyped => "Пользователь печатал.",
        InterventionKind.MouseClicked => "Клик мышью в текстовом поле.",
        InterventionKind.TextSelected => "Выделен текст.",
        InterventionKind.ClipboardPaste => "Вставка из буфера обмена.",
        InterventionKind.Undo => "Отмена действия пользователем.",
        _ => "Изменение, не инициированное VoiceFlowWin.",
    };

    private sealed class SelfInputScope : IDisposable
    {
        private readonly InputInterventionMonitor _owner;
        private bool _disposed;

        public SelfInputScope(InputInterventionMonitor owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Ожидаемая позиция обновляется до снятия блокировки: иначе опрос
            // успевал увидеть уже сдвинутую нашей же вставкой каретку рядом со
            // старым ожидаемым значением и объявлял это вмешательством.
            if (_owner._suppressDepth == 1 && _owner.IsWatching)
            {
                _owner.RefreshExpectedCaret();
            }

            Interlocked.Decrement(ref _owner._suppressDepth);
        }
    }
}
