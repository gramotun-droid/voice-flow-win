using System.Diagnostics;

namespace VoiceFlowWin.UpdaterHost;

/// <summary>
/// Отдельный процесс, который переживает завершение основного приложения.
/// </summary>
/// <remarks>
/// Приложение не может обновить само себя: его файлы заняты, пока оно
/// работает. Поэтому updater ждёт выхода VoiceFlowWin по PID, запускает
/// проверенный установщик и снова поднимает приложение. Если установщик
/// завершился с ошибкой, приложение всё равно запускается обратно — рабочая
/// версия не должна пропасть из-за неудачного обновления.
/// </remarks>
internal static class Program
{
    private static readonly TimeSpan ProcessWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать окончания установки после запуска установщика.</summary>
    private static readonly TimeSpan InstallWaitTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan FileReleaseDelay = TimeSpan.FromSeconds(2);

    private static int Main(string[] args)
    {
        var options = ParseArguments(args);

        if (options.InstallerPath is null || !File.Exists(options.InstallerPath))
        {
            Console.Error.WriteLine("Не указан или не найден файл установщика.");
            return 2;
        }

        WaitForExit(options.ProcessId);

        var installerExitCode = RunInstaller(options.InstallerPath);

        // Inno Setup распаковывает себя во временный файл и продолжает работу
        // уже в нём, поэтому запущенный процесс завершается задолго до конца
        // установки. Без этого ожидания приложение поднималось поверх ещё
        // идущей установки — и установщик его тут же закрывал.
        WaitForInstallerToFinish(options.InstallerPath);

        if (options.RelaunchPath is not null &&
            File.Exists(options.RelaunchPath) &&
            !IsApplicationRunning(options.RelaunchPath))
        {
            TryRelaunch(options.RelaunchPath);
        }

        return installerExitCode;
    }

    /// <summary>Ждёт, пока не останется процессов установщика.</summary>
    private static void WaitForInstallerToFinish(string installerPath)
    {
        // Дочерний процесс Inno Setup наследует имя установщика, отличается
        // только расширением, поэтому ищется по имени без него.
        var name = Path.GetFileNameWithoutExtension(installerPath);
        var deadline = DateTime.UtcNow + InstallWaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var running = Process.GetProcessesByName(name);
            foreach (var process in running)
            {
                process.Dispose();
            }

            if (running.Length == 0)
            {
                // Файлам нужно мгновение, чтобы освободиться после замены.
                Thread.Sleep(FileReleaseDelay);
                return;
            }

            Thread.Sleep(PollInterval);
        }

        Console.Error.WriteLine("Установщик не завершился за отведённое время.");
    }

    /// <summary>Не поднимать второй экземпляр, если приложение уже запущено.</summary>
    private static bool IsApplicationRunning(string relaunchPath)
    {
        var name = Path.GetFileNameWithoutExtension(relaunchPath);
        var running = Process.GetProcessesByName(name);
        foreach (var process in running)
        {
            process.Dispose();
        }

        return running.Length > 0;
    }

    private static void WaitForExit(int? processId)
    {
        if (processId is not { } id)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.WaitForExit((int)ProcessWaitTimeout.TotalMilliseconds))
            {
                Console.Error.WriteLine("Приложение не завершилось за отведённое время; установка продолжается.");
            }
        }
        catch (ArgumentException)
        {
            // Процесс уже завершился — это нормальный случай.
        }
    }

    private static int RunInstaller(string installerPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
            };

            // Тихая установка с сохранением пользовательских данных.
            startInfo.ArgumentList.Add("/SILENT");
            startInfo.ArgumentList.Add("/NORESTART");

            using var installer = Process.Start(startInfo);
            if (installer is null)
            {
                return 3;
            }

            installer.WaitForExit();
            return installer.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка запуска установщика: " + ex.Message);
            return 4;
        }
    }

    private static void TryRelaunch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Не удалось перезапустить приложение: " + ex.Message);
        }
    }

    private static (int? ProcessId, string? InstallerPath, string? RelaunchPath) ParseArguments(string[] args)
    {
        int? processId = null;
        string? installerPath = null;
        string? relaunchPath = null;

        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pid" when int.TryParse(args[i + 1], out var parsed):
                    processId = parsed;
                    break;
                case "--installer":
                    installerPath = args[i + 1];
                    break;
                case "--relaunch":
                    relaunchPath = args[i + 1];
                    break;
            }
        }

        return (processId, installerPath, relaunchPath);
    }
}
