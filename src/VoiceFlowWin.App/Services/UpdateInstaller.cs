using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Updater;

namespace VoiceFlowWin.App.Services;

/// <summary>
/// Запускает установку скачанного обновления.
/// </summary>
/// <remarks>
/// Установка никогда не начинается во время активной диктовки: пользователь
/// потерял бы фразу на середине. Сначала диктовка корректно завершается,
/// настройки сохраняются, модели выгружаются, и только потом запускается
/// отдельный процесс updater, который дожидается выхода приложения по PID и
/// запускает установщик. При любой ошибке остаётся работать текущая версия.
/// </remarks>
public sealed class UpdateInstaller
{
    private readonly DictationController _controller;
    private readonly UpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<UpdateInstaller> _logger;

    public UpdateInstaller(
        DictationController controller,
        UpdateService updates,
        ISettingsService settings,
        AppPaths paths,
        ILogger<UpdateInstaller> logger)
    {
        _controller = controller;
        _updates = updates;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public bool CanInstallNow => !_controller.IsDictating;

    /// <summary>Готовит приложение и запускает updater. Возвращает false, если установка невозможна.</summary>
    public bool StartInstallation(bool finishDictationFirst)
    {
        var packagePath = _updates.Status.PackagePath;
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            _logger.LogWarning("Установка запрошена, но проверенный пакет отсутствует.");
            return false;
        }

        if (!PackageVerifier.IsInsideDirectory(packagePath, _paths.UpdatesDirectory))
        {
            _logger.LogError("Пакет обновления находится вне каталога обновлений — запуск отменён.");
            return false;
        }

        if (_controller.IsDictating)
        {
            if (!finishDictationFirst)
            {
                return false;
            }

            _controller.Stop();
        }

        // Настройки должны попасть на диск до перезапуска.
        _settings.Save(_settings.Current);

        var updaterPath = Path.Combine(AppContext.BaseDirectory, "VoiceFlowWin.UpdaterHost.exe");
        if (!File.Exists(updaterPath))
        {
            _logger.LogError("Не найден процесс обновления: {Path}", updaterPath);
            return false;
        }

        try
        {
            var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VoiceFlowWin.exe");
            var startInfo = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--installer");
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add("--relaunch");
            startInfo.ArgumentList.Add(executable);

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            // Не смогли запустить updater — остаёмся на текущей версии.
            _logger.LogError(ex, "Не удалось запустить процесс обновления.");
            return false;
        }
    }
}
