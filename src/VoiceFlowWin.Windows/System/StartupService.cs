using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.Windows.System;

/// <summary>
/// Автозапуск с Windows через ветку Run текущего пользователя.
/// </summary>
/// <remarks>
/// Именно HKCU, а не HKLM: приложение ставится для текущего пользователя и
/// не требует прав администратора. Ключ переписывается при каждом включении,
/// чтобы после переустановки в другой каталог автозапуск не указывал в пустоту.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceFlowWin";

    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService>? logger = null) =>
        _logger = logger ?? NullLogger<StartupService>.Instance;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать настройку автозапуска.");
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
            {
                _logger.LogWarning("Не удалось определить путь к исполняемому файлу для автозапуска.");
                return;
            }

            // --minimized: при автозапуске окно настроек показывать не нужно.
            key.SetValue(ValueName, $"\"{executable}\" --minimized");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось изменить настройку автозапуска.");
        }
    }
}
