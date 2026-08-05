using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Windows.Input;
using Xunit;

namespace VoiceFlowWin.Windows.Tests;

/// <summary>
/// Проверяет, что собственный ввод приложения не принимается за правку
/// пользователя. Позиция каретки берётся из её экранных координат, поэтому
/// меняется от каждого напечатанного символа — на этом строятся все ловушки.
/// </summary>
public sealed class InterventionMonitorTests
{
    private static readonly WindowFocusSnapshot Baseline = new(
        WindowHandle: 100,
        ProcessId: 42,
        ProcessName: "notepad",
        WindowTitle: "Блокнот",
        FocusedControlHandle: 200,
        FocusedControlId: "Edit",
        CaretPosition: 1000,
        KeyboardLayoutId: 0x0419);

    [Fact]
    public void Сдвиг_каретки_собственной_вставкой_не_считается_вмешательством()
    {
        var tracker = new StubFocusTracker(Baseline);
        using var hook = new LowLevelKeyboardHook();
        using var monitor = new InputInterventionMonitor(hook, tracker);

        monitor.StartWatching(Baseline);

        // Приложение печатает: каретка уезжает вправо, как и должна.
        using (monitor.SuppressSelfInput())
        {
            tracker.Snapshot = tracker.Snapshot with { CaretPosition = 1040 };
        }

        Assert.Equal(InterventionKind.None, monitor.CheckNow(Baseline));
    }

    [Fact]
    public void Каретка_доехавшая_после_вставки_не_считается_вмешательством()
    {
        var tracker = new StubFocusTracker(Baseline);
        using var hook = new LowLevelKeyboardHook();
        using var monitor = new InputInterventionMonitor(hook, tracker);

        monitor.StartWatching(Baseline);

        using (monitor.SuppressSelfInput())
        {
            tracker.Snapshot = tracker.Snapshot with { CaretPosition = 1040 };
        }

        // Поле обновляет координаты каретки уже после возврата из SendInput.
        tracker.Snapshot = tracker.Snapshot with { CaretPosition = 1060 };

        Assert.Equal(InterventionKind.None, monitor.CheckNow(Baseline));
    }

    [Fact]
    public void Пустой_снимок_фокуса_не_считается_вмешательством()
    {
        var tracker = new StubFocusTracker(WindowFocusSnapshot.Unknown);
        using var hook = new LowLevelKeyboardHook();
        using var monitor = new InputInterventionMonitor(hook, tracker);

        monitor.StartWatching(Baseline);

        Assert.Equal(InterventionKind.None, monitor.CheckNow(Baseline));
    }

    [Fact]
    public void Смена_окна_остаётся_вмешательством()
    {
        var tracker = new StubFocusTracker(Baseline);
        using var hook = new LowLevelKeyboardHook();
        using var monitor = new InputInterventionMonitor(hook, tracker);

        monitor.StartWatching(Baseline);
        tracker.Snapshot = tracker.Snapshot with { WindowHandle = 999, ProcessId = 7 };

        Assert.Equal(InterventionKind.WindowChanged, monitor.CheckNow(Baseline));
    }

    [Fact]
    public void Устойчивый_сдвиг_каретки_после_паузы_считается_вмешательством()
    {
        var time = new ManualTimeProvider();
        var tracker = new StubFocusTracker(Baseline);
        using var hook = new LowLevelKeyboardHook();
        using var monitor = new InputInterventionMonitor(hook, tracker, logger: null, timeProvider: time);

        monitor.StartWatching(Baseline);

        using (monitor.SuppressSelfInput())
        {
            tracker.Snapshot = tracker.Snapshot with { CaretPosition = 1040 };
        }

        // Пользователь щёлкнул мышью заметно позже собственной вставки.
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.Snapshot = tracker.Snapshot with { CaretPosition = 500 };

        // Вмешательство объявляется по устойчивому расхождению — со второго
        // опроса. Монитор опрашивает состояние и сам, по таймеру, поэтому
        // проверяется итог, а не результат конкретного вызова.
        monitor.CheckNow(Baseline);
        Assert.Equal(InterventionKind.CaretMoved, monitor.CheckNow(Baseline));
    }

    private sealed class StubFocusTracker : IFocusTracker
    {
        public StubFocusTracker(WindowFocusSnapshot snapshot) => Snapshot = snapshot;

        public WindowFocusSnapshot Snapshot { get; set; }

        public WindowFocusSnapshot Capture() => Snapshot;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta) => _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
    }
}
