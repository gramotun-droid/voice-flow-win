namespace VoiceFlowWin.Core.Models;

/// <summary>
/// Снимок «куда мы печатаем»: окно, элемент ввода, позиция каретки и раскладка.
/// Сравнение двух снимков — основной способ понять, что пользователь вмешался.
/// </summary>
public sealed record WindowFocusSnapshot(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    nint FocusedControlHandle,
    string FocusedControlId,
    int CaretPosition,
    int KeyboardLayoutId)
{
    public static readonly WindowFocusSnapshot Unknown =
        new(0, 0, string.Empty, string.Empty, 0, string.Empty, -1, 0);

    public RecognitionLanguage LayoutLanguage => RecognitionLanguageExtensions.FromLayoutId(KeyboardLayoutId);

    /// <summary>
    /// Совпадает ли цель ввода. Заголовок окна и раскладка сознательно не
    /// учитываются: заголовок меняется сам по себе (например, в браузере), а
    /// смена раскладки не должна ломать уже начатый сегмент.
    /// </summary>
    public bool IsSameTarget(WindowFocusSnapshot other) =>
        WindowHandle == other.WindowHandle &&
        ProcessId == other.ProcessId &&
        FocusedControlHandle == other.FocusedControlHandle &&
        string.Equals(FocusedControlId, other.FocusedControlId, StringComparison.Ordinal);

    /// <summary>
    /// Каретка там же, где мы её оставили. Позиция -1 означает «неизвестно» —
    /// такие случаи не считаются перемещением, иначе приложения без UI
    /// Automation постоянно давали бы ложные срабатывания.
    /// </summary>
    public bool IsSameCaret(WindowFocusSnapshot other) =>
        CaretPosition < 0 || other.CaretPosition < 0 || CaretPosition == other.CaretPosition;
}
