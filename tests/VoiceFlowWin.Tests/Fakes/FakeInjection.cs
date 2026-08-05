using System.Text;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Core.Text;

namespace VoiceFlowWin.Tests.Fakes;

/// <summary>
/// Имитирует текстовое поле чужого приложения: хранит содержимое и позицию
/// каретки, поэтому тест видит ровно то, что увидел бы пользователь.
/// </summary>
public sealed class FakeTextField
{
    private readonly StringBuilder _content = new();

    public string Content => _content.ToString();

    /// <summary>Сколько раз приложение отправляло Backspace.</summary>
    public int TotalDeletedElements { get; private set; }

    public void Type(string text) => _content.Append(text);

    public void Backspace(int elementCount)
    {
        TotalDeletedElements += elementCount;
        var elements = TextElements.Split(_content.ToString());
        var keep = Math.Max(0, elements.Count - elementCount);
        var kept = TextElements.Substring(_content.ToString(), 0, keep);
        _content.Clear();
        _content.Append(kept);
    }

    /// <summary>Пользователь сам что-то напечатал.</summary>
    public void UserTypes(string text) => _content.Append(text);
}

public sealed class FakeInjectionService : ITextInjectionService
{
    private readonly FakeTextField _field;

    public FakeInjectionService(FakeTextField field) => _field = field;

    public TextInjectionMode Mode { get; set; } = TextInjectionMode.SendInput;

    public bool FailNextInjection { get; set; }

    public InjectionFailureKind FailureKind { get; set; } = InjectionFailureKind.PrivilegeBlocked;

    public Task<InjectionResult> InjectAsync(string text, CancellationToken cancellationToken)
    {
        if (FailNextInjection)
        {
            FailNextInjection = false;
            return Task.FromResult(InjectionResult.Fail(FailureKind, "Тестовый отказ вставки."));
        }

        _field.Type(text);
        return Task.FromResult(InjectionResult.Ok(text));
    }

    public Task<bool> DeleteBackwardAsync(int elementCount, CancellationToken cancellationToken)
    {
        _field.Backspace(elementCount);
        return Task.FromResult(true);
    }
}

public sealed class FakeFocusTracker : IFocusTracker
{
    public WindowFocusSnapshot Snapshot { get; set; } = new(
        WindowHandle: 100,
        ProcessId: 42,
        ProcessName: "notepad",
        WindowTitle: "Безымянный — Блокнот",
        FocusedControlHandle: 200,
        FocusedControlId: "Edit",
        CaretPosition: 0,
        KeyboardLayoutId: 0x0419);

    public WindowFocusSnapshot Capture() => Snapshot;

    public void SwitchWindow() => Snapshot = Snapshot with { WindowHandle = Snapshot.WindowHandle + 1, ProcessId = Snapshot.ProcessId + 1 };

    public void MoveCaret(int position) => Snapshot = Snapshot with { CaretPosition = position };

    public void SwitchLayout(int layoutId) => Snapshot = Snapshot with { KeyboardLayoutId = layoutId };
}

public sealed class FakeInterventionMonitor : IInputInterventionMonitor
{
    private readonly FakeFocusTracker _tracker;

    public FakeInterventionMonitor(FakeFocusTracker tracker) => _tracker = tracker;

    public bool IsWatching { get; private set; }

    public int SuppressCount { get; private set; }

    /// <summary>Принудительное вмешательство, которое тест выставляет вручную.</summary>
    public InterventionKind Forced { get; set; } = InterventionKind.None;

    public event EventHandler<InterventionEventArgs>? InterventionDetected;

    public void StartWatching(WindowFocusSnapshot baseline) => IsWatching = true;

    public void StopWatching() => IsWatching = false;

    public IDisposable SuppressSelfInput()
    {
        SuppressCount++;
        return new Suppression();
    }

    public InterventionKind CheckNow(WindowFocusSnapshot baseline)
    {
        if (Forced != InterventionKind.None)
        {
            return Forced;
        }

        var current = _tracker.Capture();
        if (!baseline.IsSameTarget(current))
        {
            return InterventionKind.WindowChanged;
        }

        return InterventionKind.None;
    }

    public void Raise(InterventionKind kind) =>
        InterventionDetected?.Invoke(this, new InterventionEventArgs(kind, kind.ToString()));

    public void Dispose() => IsWatching = false;

    private sealed class Suppression : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Load() => Current;

    public void Save(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, settings);
    }

    public void Mutate(Action<AppSettings> mutate)
    {
        mutate(Current);
        SettingsChanged?.Invoke(this, Current);
    }
}
