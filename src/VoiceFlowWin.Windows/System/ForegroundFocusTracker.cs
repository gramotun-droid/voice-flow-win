using System.Diagnostics;
using System.Runtime.Versioning;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.System;

/// <summary>
/// Определяет активное окно, элемент ввода, позицию каретки и раскладку.
/// </summary>
/// <remarks>
/// Позиция каретки берётся из GUITHREADINFO: там же лежит и hwndFocus, поэтому
/// одним вызовом видно и элемент ввода, и место курсора. UI Automation дала бы
/// более точный идентификатор элемента, но стоит заметно дороже на каждый
/// опрос, а для нашей задачи достаточно пары «окно + элемент + прямоугольник
/// каретки»: любое изменение любого из них означает, что печатать вслепую
/// больше нельзя.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ForegroundFocusTracker : IFocusTracker
{
    public WindowFocusSnapshot Capture()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            return WindowFocusSnapshot.Unknown;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var layout = NativeMethods.GetKeyboardLayout(threadId);

        var guiInfo = new NativeMethods.GUITHREADINFO();
        guiInfo.cbSize = global::System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>();

        nint focusedControl = 0;
        var caretPosition = -1;

        if (NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo))
        {
            focusedControl = guiInfo.hwndFocus != 0 ? guiInfo.hwndFocus : guiInfo.hwndActive;

            // Точную позицию в тексте узнать нельзя, но координаты каретки
            // меняются при любом её перемещении — этого достаточно.
            if (guiInfo.hwndCaret != 0)
            {
                caretPosition = (guiInfo.rcCaret.Left << 16) ^ guiInfo.rcCaret.Top;
            }
        }

        return new WindowFocusSnapshot(
            WindowHandle: window,
            ProcessId: (int)processId,
            ProcessName: GetProcessName((int)processId),
            WindowTitle: GetWindowTitle(window),
            FocusedControlHandle: focusedControl,
            FocusedControlId: GetClassName(focusedControl),
            CaretPosition: caretPosition,
            KeyboardLayoutId: (int)layout);
    }

    private static string GetWindowTitle(nint window)
    {
        var buffer = new char[512];
        var length = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetClassName(nint control)
    {
        if (control == 0)
        {
            return string.Empty;
        }

        var buffer = new char[256];
        var length = NativeMethods.GetClassName(control, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Процесс успел завершиться между вызовами — не повод падать.
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
