using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Работа с буфером обмена для резервного способа вставки.
/// </summary>
/// <remarks>
/// Буфер обмена принадлежит пользователю, а не приложению, поэтому прежнее
/// содержимое сохраняется целиком (со всеми форматами, а не только текстом) и
/// возвращается обратно. Если пользователь успел скопировать что-то своё, пока
/// шла вставка, восстановление отменяется — иначе мы затрём его копирование.
/// Все операции идут в отдельном STA-потоке: Clipboard API этого требует.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ClipboardService
{
    private const int RetryCount = 5;
    private const int RetryDelayMs = 40;

    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(ILogger<ClipboardService>? logger = null) =>
        _logger = logger ?? NullLogger<ClipboardService>.Instance;

    /// <summary>Снимок буфера, который можно вернуть на место.</summary>
    public sealed class ClipboardSnapshot
    {
        internal ClipboardSnapshot(IDataObject? data, string? textFingerprint)
        {
            Data = data;
            TextFingerprint = textFingerprint;
        }

        internal IDataObject? Data { get; }

        /// <summary>Текстовое содержимое на момент снимка — по нему проверяется, не сменил ли буфер пользователь.</summary>
        internal string? TextFingerprint { get; }

        public bool IsEmpty => Data is null;
    }

    public ClipboardSnapshot Capture() => RunSta(() =>
    {
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null)
                {
                    return new ClipboardSnapshot(null, null);
                }

                var text = data.GetDataPresent(DataFormats.UnicodeText)
                    ? data.GetData(DataFormats.UnicodeText) as string
                    : null;

                return new ClipboardSnapshot(data, text);
            }
            catch (ExternalException)
            {
                // Буфер занят другим приложением — ждём и пробуем снова.
                Thread.Sleep(RetryDelayMs);
            }
        }

        _logger.LogWarning("Не удалось прочитать буфер обмена: он занят другим приложением.");
        return new ClipboardSnapshot(null, null);
    });

    public bool SetText(string text) => RunSta(() =>
    {
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    });

    /// <summary>Возвращает прежнее содержимое, если пользователь не заменил его сам.</summary>
    public bool Restore(ClipboardSnapshot snapshot, string injectedText)
    {
        if (snapshot.IsEmpty)
        {
            return false;
        }

        return RunSta(() =>
        {
            try
            {
                var current = Clipboard.GetDataObject();
                var currentText = current?.GetDataPresent(DataFormats.UnicodeText) == true
                    ? current.GetData(DataFormats.UnicodeText) as string
                    : null;

                // В буфере уже не наш текст: пользователь скопировал своё,
                // восстанавливать старое значение нельзя.
                if (!string.Equals(currentText, injectedText, StringComparison.Ordinal))
                {
                    return false;
                }

                Clipboard.SetDataObject(snapshot.Data!, copy: true);
                return true;
            }
            catch (ExternalException ex)
            {
                _logger.LogWarning(ex, "Не удалось восстановить буфер обмена.");
                return false;
            }
        });
    }

    public void Clear() => RunSta(() =>
    {
        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Не критично: буфер занят.
        }

        return true;
    });

    private static T RunSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return action();
        }

        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
