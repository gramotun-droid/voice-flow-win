using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoiceFlowWin.Windows.System;

/// <summary>
/// Ставит системный курсор ожидания на время долгой операции.
/// </summary>
/// <remarks>
/// Курсор меняется системно, а не только над своими окнами: пока идёт проход
/// по всей диктовке, приложение переписывает текст в чужом поле, и пользователь
/// должен видеть, что работа ещё идёт, где бы ни находился указатель. Ручная
/// правка в этот момент отменила бы замену.
///
/// Системный курсор — общий ресурс, поэтому он обязательно возвращается на
/// место: и по завершении операции, и при выходе из процесса. Иначе стрелка
/// осталась бы «песочными часами» во всей системе.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BusyCursorScope : IDisposable
{
    private const uint OCR_NORMAL = 32512;
    private const uint OCR_IBEAM = 32513;
    private const int IDC_WAIT = 32514;
    private const int IDC_APPSTARTING = 32650;
    private const uint SPI_SETCURSORS = 0x0057;

    private readonly ILogger _logger;
    private bool _restored;

    private BusyCursorScope(ILogger logger) => _logger = logger;

    /// <summary>Включает курсор ожидания. Вернуть прежний вид — задача Dispose.</summary>
    public static BusyCursorScope Begin(ILogger? logger = null)
    {
        var scope = new BusyCursorScope(logger ?? NullLogger.Instance);
        scope.Apply();
        return scope;
    }

    private void Apply()
    {
        try
        {
            // SetSystemCursor забирает переданный курсор себе, поэтому для
            // каждой замены загружается собственная копия.
            Replace(OCR_NORMAL, IDC_APPSTARTING);
            Replace(OCR_IBEAM, IDC_WAIT);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Системный курсор изменить не удалось.");
            _restored = true;
        }
    }

    private static void Replace(uint targetId, int systemCursorId)
    {
        var cursor = LoadCursor(0, systemCursorId);
        if (cursor != 0)
        {
            var copy = CopyIcon(cursor);
            if (copy != 0)
            {
                SetSystemCursor(copy, targetId);
            }
        }
    }

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;

        // Возврат всех системных курсоров из текущей схемы: точечная замена
        // обратно потребовала бы хранить прежние handles, а они уже переданы
        // системе и могли быть освобождены.
        SystemParametersInfo(SPI_SETCURSORS, 0, 0, 0);
    }

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursor(nint hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint CopyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(nint hCursor, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);
}
