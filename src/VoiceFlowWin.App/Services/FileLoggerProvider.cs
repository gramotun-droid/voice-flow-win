using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VoiceFlowWin.App.Services;

/// <summary>
/// Простой файловый лог с ротацией по размеру.
/// </summary>
/// <remarks>
/// В лог пишутся только технические события: состояния, ошибки, причины
/// заморозки сегмента. Распознанный текст и содержимое полей пользователя
/// сюда не попадают никогда — это требование к конфиденциальности, а не
/// вопрос удобства отладки.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    private readonly string _filePath;
    private readonly object _sync = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_sync)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Невозможность записать лог не должна мешать работе приложения.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < MaxFileSizeBytes)
        {
            return;
        }

        var previous = _filePath + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(_filePath, previous);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = _category[(_category.LastIndexOf('.') + 1)..];
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {shortCategory}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}
