using System.Runtime.Versioning;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Остановка диктовки клавишей Esc.
/// </summary>
/// <remarks>
/// Esc — отдельная системная клавиша, а не пользовательская настройка: он
/// обязан останавливать диктовку всегда, в любом режиме, независимо от того,
/// какое сочетание назначено основным.
///
/// Перехват включается только на время активной диктовки. Вне её приложение
/// не трогает Esc вообще — иначе оно ломало бы обычную работу с диалогами и
/// меню. Во время диктовки нажатие гасится, чтобы то же самое нажатие не
/// закрыло заодно окно, в котором пользователь диктует.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EscapeStopService : IEscapeStopService
{
    private readonly LowLevelKeyboardHook _hook;
    private bool _suppressKey;
    private bool _disposed;

    public EscapeStopService(LowLevelKeyboardHook hook)
    {
        _hook = hook;
        _hook.KeyEvent += OnKeyEvent;
    }

    public bool IsArmed { get; private set; }

    public event EventHandler? EscapePressed;

    public void Arm(bool suppressKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _suppressKey = suppressKey;
        _hook.Start();
        IsArmed = true;
    }

    public void Disarm() => IsArmed = false;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsArmed = false;
        _hook.KeyEvent -= OnKeyEvent;
    }

    private void OnKeyEvent(object? sender, LowLevelKeyEventArgs e)
    {
        if (!IsArmed || e.IsInjected || !e.IsKeyDown || e.VirtualKey != NativeMethods.VK_ESCAPE)
        {
            return;
        }

        if (_suppressKey)
        {
            e.Handled = true;
        }

        EscapePressed?.Invoke(this, EventArgs.Empty);
    }
}
