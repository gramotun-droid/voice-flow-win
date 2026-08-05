using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Глобальная горячая клавиша вызова диктовки.
/// </summary>
/// <remarks>
/// Два разных механизма под две разные задачи:
///
/// RegisterHotKey — для Toggle с модификаторами. Система сама гарантирует
/// эксклюзивность, сообщает о конфликте с другим приложением и не даёт
/// сочетанию просочиться в активное окно лишним символом.
///
/// Низкоуровневый хук — для push-to-talk и одиночных клавиш. RegisterHotKey
/// не сообщает об отпускании клавиши, а без этого удержание не реализовать.
///
/// В обоих случаях хранится virtual key code, а не символ, поэтому сочетание
/// не «переезжает» при смене раскладки.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0xB0F1;

    private readonly LowLevelKeyboardHook _hook;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly object _sync = new();

    private HotkeyWindow? _window;
    private DictationActivationMode _mode;
    private bool _useHook;
    private bool _isPressed;
    private bool _disposed;

    public GlobalHotkeyService(LowLevelKeyboardHook hook, ILogger<GlobalHotkeyService>? logger = null)
    {
        _hook = hook;
        _logger = logger ?? NullLogger<GlobalHotkeyService>.Instance;
        _hook.KeyEvent += OnKeyEvent;
    }

    public HotkeyDefinition? Current { get; private set; }

    public event EventHandler<HotkeyEventArgs>? HotkeyTriggered;

    public bool Register(HotkeyDefinition hotkey, DictationActivationMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!hotkey.IsAllowedAsPrimary())
        {
            _logger.LogWarning("Esc нельзя назначить основной клавишей вызова.");
            return false;
        }

        lock (_sync)
        {
            Unregister();
            _mode = mode;

            // Push-to-talk и одиночные клавиши обслуживает только хук.
            _useHook = mode == DictationActivationMode.PushToTalk || !hotkey.HasModifiers;

            if (_useHook)
            {
                _hook.Start();
                Current = hotkey;
                return true;
            }

            _window ??= new HotkeyWindow(OnHotkeyMessage);
            if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, ToNativeModifiers(hotkey.Modifiers), (uint)hotkey.VirtualKey))
            {
                _logger.LogWarning("Сочетание {Hotkey} уже занято другим приложением.", hotkey.ToDisplayString());
                return false;
            }

            Current = hotkey;
            return true;
        }
    }

    public void Unregister()
    {
        lock (_sync)
        {
            if (_window is not null)
            {
                NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            }

            Current = null;
            _isPressed = false;
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
        Unregister();
        _window?.DestroyHandle();
        _window = null;
    }

    private void OnHotkeyMessage() => HotkeyTriggered?.Invoke(this, new HotkeyEventArgs(isPressed: true));

    private void OnKeyEvent(object? sender, LowLevelKeyEventArgs e)
    {
        if (!_useHook || Current is null || e.IsInjected)
        {
            return;
        }

        if (e.VirtualKey != Current.VirtualKey || !AreModifiersPressed(Current.Modifiers))
        {
            return;
        }

        if (e.IsKeyDown)
        {
            // Windows повторяет keydown при удержании — для push-to-talk важно
            // отреагировать ровно один раз.
            if (_isPressed)
            {
                e.Handled = true;
                return;
            }

            _isPressed = true;
            e.Handled = true;
            HotkeyTriggered?.Invoke(this, new HotkeyEventArgs(isPressed: true));
            return;
        }

        if (!_isPressed)
        {
            return;
        }

        _isPressed = false;
        e.Handled = true;

        if (_mode == DictationActivationMode.PushToTalk)
        {
            HotkeyTriggered?.Invoke(this, new HotkeyEventArgs(isPressed: false));
        }
    }

    private static bool AreModifiersPressed(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None)
        {
            return true;
        }

        var control = IsDown(NativeMethods.VK_CONTROL);
        var alt = IsDown(NativeMethods.VK_MENU);
        var shift = IsDown(NativeMethods.VK_SHIFT);
        var win = IsDown(NativeMethods.VK_LWIN);

        return control == modifiers.HasFlag(HotkeyModifiers.Control)
            && alt == modifiers.HasFlag(HotkeyModifiers.Alt)
            && shift == modifiers.HasFlag(HotkeyModifiers.Shift)
            && win == modifiers.HasFlag(HotkeyModifiers.Windows);
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        // MOD_NOREPEAT: без него удержание сочетания сыплет событиями.
        var result = NativeMethods.MOD_NOREPEAT;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= NativeMethods.MOD_ALT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= NativeMethods.MOD_CONTROL;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= NativeMethods.MOD_SHIFT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= NativeMethods.MOD_WIN;
        }

        return result;
    }

    /// <summary>Скрытое окно, которое принимает WM_HOTKEY.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action _onHotkey;

        public HotkeyWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                _onHotkey();
            }

            base.WndProc(ref m);
        }
    }
}
