using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Abstractions;

public enum InjectionFailureKind
{
    None,

    /// <summary>Целевое окно запущено с более высокими правами — UIPI блокирует ввод.</summary>
    PrivilegeBlocked,

    /// <summary>Буфер обмена занят другим приложением.</summary>
    ClipboardBusy,

    /// <summary>Активное окно или элемент ввода сменились прямо во время вставки.</summary>
    TargetChanged,

    Unknown,
}

/// <param name="InjectedText">Что реально ушло в приложение — именно это учитывается при последующей замене.</param>
public readonly record struct InjectionResult(bool Success, string InjectedText, InjectionFailureKind Failure = InjectionFailureKind.None, string? Message = null)
{
    public static InjectionResult Ok(string text) => new(true, text);

    public static InjectionResult Fail(InjectionFailureKind kind, string message) => new(false, string.Empty, kind, message);
}

/// <summary>
/// Ввод текста в активное окно.
/// </summary>
/// <remarks>
/// Основной путь — Unicode SendInput: он не зависит от раскладки, поэтому
/// кириллица вводится и при английской раскладке. Резервный путь — буфер
/// обмена с Ctrl+V и восстановлением прежнего содержимого.
/// </remarks>
public interface ITextInjectionService
{
    TextInjectionMode Mode { get; set; }

    /// <summary>Печатает текст в активное окно.</summary>
    Task<InjectionResult> InjectAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Удаляет <paramref name="elementCount"/> текстовых элементов слева от каретки.
    /// Вызывается только когда приложение уверено, что удаляет собственный текст.
    /// </summary>
    Task<bool> DeleteBackwardAsync(int elementCount, CancellationToken cancellationToken);
}
