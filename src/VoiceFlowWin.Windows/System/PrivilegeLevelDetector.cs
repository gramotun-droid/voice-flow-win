using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.System;

/// <summary>
/// Определяет, не заблокирует ли UIPI ввод в целевое окно.
/// </summary>
/// <remarks>
/// Windows не позволяет процессу с обычными правами слать синтетический ввод
/// в окно процесса с более высоким уровнем целостности. Проверка нужна, чтобы
/// показать пользователю понятное объяснение вместо молчаливой потери текста,
/// и чтобы не долбиться в такое окно бесконечными попытками.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PrivilegeLevelDetector : IPrivilegeLevelDetector
{
    private const int TokenElevation = 20;

    public PrivilegeLevelDetector()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsElevated { get; }

    public bool IsTargetElevated(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            // Нет доступа даже к базовой информации — почти всегда это признак
            // процесса с более высокими правами.
            return Marshal.GetLastWin32Error() == 5;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
            {
                return Marshal.GetLastWin32Error() == 5;
            }

            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        TokenElevation,
                        out var elevation,
                        Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>(),
                        out _))
                {
                    return false;
                }

                return elevation.TokenIsElevated != 0 && !IsElevated;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
