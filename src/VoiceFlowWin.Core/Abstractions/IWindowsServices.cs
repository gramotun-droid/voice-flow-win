using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Abstractions;

/// <summary>Кто сейчас в фокусе и какая у него раскладка.</summary>
public interface IFocusTracker
{
    WindowFocusSnapshot Capture();
}

/// <summary>Прямоугольник системной текстовой каретки в экранных пикселях.</summary>
public readonly record struct CaretScreenBounds(int Left, int Top, int Right, int Bottom);

/// <summary>Находит экранную позицию каретки активного поля ввода.</summary>
public interface ICaretPositionProvider
{
    /// <remarks>
    /// Некоторые приложения рисуют собственную каретку и не сообщают её
    /// Windows. В таком случае метод возвращает false, а интерфейс просто не
    /// показывает привязанный к каретке индикатор.
    /// </remarks>
    bool TryGetCaretBounds(out CaretScreenBounds bounds);
}

/// <summary>Причина, по которой сегмент нужно заморозить.</summary>
public enum InterventionKind
{
    None,
    WindowChanged,
    FocusedControlChanged,
    CaretMoved,
    UserTyped,
    MouseClicked,
    TextSelected,
    ClipboardPaste,
    Undo,
}

public sealed class InterventionEventArgs : EventArgs
{
    public InterventionEventArgs(InterventionKind kind, string description)
    {
        Kind = kind;
        Description = description;
    }

    public InterventionKind Kind { get; }

    public string Description { get; }
}

/// <summary>
/// Следит за тем, чтобы приложение не переписало текст, который пользователь
/// правил сам. Работает только во время активного сегмента.
/// </summary>
public interface IInputInterventionMonitor : IDisposable
{
    bool IsWatching { get; }

    event EventHandler<InterventionEventArgs>? InterventionDetected;

    /// <summary>Начинает наблюдение от точки, где приложение начало печатать.</summary>
    void StartWatching(WindowFocusSnapshot baseline);

    void StopWatching();

    /// <summary>Сообщает монитору, что следующий ввод инициирован самим приложением.</summary>
    IDisposable SuppressSelfInput();

    /// <summary>Проверяет прямо сейчас, изменилась ли цель ввода.</summary>
    InterventionKind CheckNow(WindowFocusSnapshot baseline);
}

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(bool isPressed) => IsPressed = isPressed;

    /// <summary>true — клавиша нажата, false — отпущена (нужно для push-to-talk).</summary>
    public bool IsPressed { get; }
}

/// <summary>Глобальная горячая клавиша вызова диктовки.</summary>
public interface IGlobalHotkeyService : IDisposable
{
    HotkeyDefinition? Current { get; }

    event EventHandler<HotkeyEventArgs>? HotkeyTriggered;

    /// <summary>Регистрирует сочетание. Возвращает false при конфликте с другим приложением.</summary>
    bool Register(HotkeyDefinition hotkey, DictationActivationMode mode);

    void Unregister();
}

/// <summary>
/// Esc как отдельная системная клавиша остановки.
/// </summary>
/// <remarks>
/// Перехват включается только на время активной диктовки — вне её Esc должен
/// работать как обычно. Во время диктовки нажатие гасится, чтобы не закрылось
/// окно, диалог или меню активного приложения.
/// </remarks>
public interface IEscapeStopService : IDisposable
{
    bool IsArmed { get; }

    event EventHandler? EscapePressed;

    void Arm(bool suppressKey);

    void Disarm();
}

/// <summary>Автозапуск с Windows.</summary>
public interface IStartupService
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

/// <summary>Уровень прав процесса — нужен для понятного сообщения об UIPI.</summary>
public interface IPrivilegeLevelDetector
{
    bool IsElevated { get; }

    /// <summary>Запущено ли окно с более высокими правами, чем VoiceFlowWin.</summary>
    bool IsTargetElevated(nint windowHandle);
}
