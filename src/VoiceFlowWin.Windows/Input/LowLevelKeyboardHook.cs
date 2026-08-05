using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

public sealed class LowLevelKeyEventArgs : EventArgs
{
    public LowLevelKeyEventArgs(int virtualKey, bool isKeyDown, bool isInjected)
    {
        VirtualKey = virtualKey;
        IsKeyDown = isKeyDown;
        IsInjected = isInjected;
    }

    public int VirtualKey { get; }

    public bool IsKeyDown { get; }

    /// <summary>Событие создано программно, в том числе самим VoiceFlowWin.</summary>
    public bool IsInjected { get; }

    /// <summary>Если выставить в true, нажатие не дойдёт до активного приложения.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Общий низкоуровневый клавиатурный хук.
/// </summary>
/// <remarks>
/// Хук один на всё приложение: и горячая клавиша в режиме push-to-talk, и
/// остановка по Esc слушают его события. Два независимых хука конкурировали бы
/// за одно и то же нажатие и мешали друг другу гасить клавишу.
///
/// Живёт на отдельном потоке с собственным циклом сообщений: WH_KEYBOARD_LL
/// вызывается в потоке, установившем хук, и если этот поток занят (например,
/// отрисовкой окна), Windows молча снимет хук по таймауту.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly ILogger<LowLevelKeyboardHook> _logger;
    private readonly object _sync = new();

    // Делегат обязан жить столько же, сколько хук: иначе GC соберёт его и
    // первое же нажатие уронит процесс.
    private NativeMethods.HookProc? _callback;
    private nint _hookHandle;
    private Thread? _thread;
    private uint _nativeThreadId;
    private bool _disposed;

    public LowLevelKeyboardHook(ILogger<LowLevelKeyboardHook>? logger = null) =>
        _logger = logger ?? NullLogger<LowLevelKeyboardHook>.Instance;

    public event EventHandler<LowLevelKeyEventArgs>? KeyEvent;

    public bool IsRunning => _hookHandle != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_thread is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => RunMessageLoop(ready))
            {
                IsBackground = true,
                Name = "VoiceFlowWin.KeyboardHook",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_thread is null)
            {
                return;
            }

            if (_nativeThreadId != 0)
            {
                // WM_QUIT адресно завершает цикл сообщений именно этого потока;
                // Application.Exit() остановил бы все циклы в процессе.
                NativeMethods.PostThreadMessage(_nativeThreadId, NativeMethods.WM_QUIT, 0, 0);
            }

            _thread.Join(TimeSpan.FromSeconds(2));
            _thread = null;
            _nativeThreadId = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void RunMessageLoop(ManualResetEventSlim ready)
    {
        _nativeThreadId = NativeMethods.GetCurrentThreadId();
        _callback = HookCallback;

        var module = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, module, 0);

        if (_hookHandle == 0)
        {
            _logger.LogError("Не удалось установить клавиатурный хук: ошибка {Error}.", Marshal.GetLastWin32Error());
            ready.Set();
            return;
        }

        ready.Set();

        try
        {
            Application.Run();
        }
        finally
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
            _callback = null;
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var isSelfInput = InputMarker.IsSelfInput(data.dwExtraInfo);

        var args = new LowLevelKeyEventArgs((int)data.vkCode, isKeyDown, isSelfInput);

        try
        {
            KeyEvent?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            // Исключение внутри хука снимает его для всей системы.
            _logger.LogError(ex, "Ошибка обработчика клавиатурного хука.");
        }

        if (args.Handled)
        {
            return 1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }
}
