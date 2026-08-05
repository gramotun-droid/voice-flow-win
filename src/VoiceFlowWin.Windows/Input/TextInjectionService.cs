using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Core.Text;
using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Вставка текста в активное окно.
/// </summary>
/// <remarks>
/// Основной путь — Unicode SendInput: он не зависит от раскладки и работает
/// в подавляющем большинстве приложений. Резервный — буфер обмена с Ctrl+V:
/// его понимают даже те редакторы, которые игнорируют синтетический ввод, но
/// он трогает пользовательский буфер, поэтому включается только когда
/// SendInput не сработал.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TextInjectionService : ITextInjectionService
{
    private readonly ClipboardService _clipboard;
    private readonly ISettingsService _settings;
    private readonly IPrivilegeLevelDetector _privileges;
    private readonly ILogger<TextInjectionService> _logger;

    public TextInjectionService(
        ClipboardService clipboard,
        ISettingsService settings,
        IPrivilegeLevelDetector privileges,
        ILogger<TextInjectionService>? logger = null)
    {
        _clipboard = clipboard;
        _settings = settings;
        _privileges = privileges;
        _logger = logger ?? NullLogger<TextInjectionService>.Instance;
        Mode = settings.Current.Injection.Mode;
    }

    public TextInjectionMode Mode { get; set; }

    public async Task<InjectionResult> InjectAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InjectionResult.Ok(string.Empty);
        }

        var target = NativeMethods.GetForegroundWindow();
        if (target != 0 && _privileges.IsTargetElevated(target) && !_privileges.IsElevated)
        {
            // UIPI: обычный процесс не может слать ввод в окно администратора.
            // Пытаться бесполезно, поэтому сразу сообщаем понятную причину.
            return InjectionResult.Fail(
                InjectionFailureKind.PrivilegeBlocked,
                "Активное окно запущено от имени администратора. Перезапустите VoiceFlowWin от администратора, чтобы вводить текст в это окно.");
        }

        return Mode switch
        {
            TextInjectionMode.Clipboard => await InjectViaClipboardAsync(text, cancellationToken).ConfigureAwait(false),
            TextInjectionMode.Compatibility => await InjectViaSendInputAsync(text, perCharacter: true, cancellationToken).ConfigureAwait(false),
            TextInjectionMode.SendInput => await InjectViaSendInputAsync(text, perCharacter: false, cancellationToken).ConfigureAwait(false),
            _ => await InjectWithFallbackAsync(text, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<bool> DeleteBackwardAsync(int elementCount, CancellationToken cancellationToken)
    {
        if (elementCount <= 0)
        {
            return true;
        }

        var inputs = UnicodeInputBuilder.BuildBackspaceInput(elementCount);
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != inputs.Length)
        {
            _logger.LogWarning(
                "SendInput отправил {Sent} из {Total} нажатий Backspace (ошибка {Error}).",
                sent,
                inputs.Length,
                Marshal.GetLastWin32Error());
            return false;
        }

        await DelayAsync(_settings.Current.Injection.KeystrokeDelayMs, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<InjectionResult> InjectWithFallbackAsync(string text, CancellationToken cancellationToken)
    {
        var result = await InjectViaSendInputAsync(text, perCharacter: false, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return result;
        }

        _logger.LogInformation("SendInput не сработал, пробуем буфер обмена.");
        return await InjectViaClipboardAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InjectionResult> InjectViaSendInputAsync(string text, bool perCharacter, CancellationToken cancellationToken)
    {
        var delayMs = _settings.Current.Injection.KeystrokeDelayMs;

        if (perCharacter)
        {
            // Режим совместимости: некоторые приложения теряют символы, если
            // прислать всю строку одним пакетом.
            foreach (var element in TextElements.Split(text))
            {
                if (!SendChunk(element))
                {
                    return SendInputFailure();
                }

                await DelayAsync(Math.Max(delayMs, 1), cancellationToken).ConfigureAwait(false);
            }

            return InjectionResult.Ok(text);
        }

        if (!SendChunk(text))
        {
            return SendInputFailure();
        }

        await DelayAsync(delayMs, cancellationToken).ConfigureAwait(false);
        return InjectionResult.Ok(text);
    }

    private static bool SendChunk(string chunk)
    {
        if (chunk.Length == 0)
        {
            return true;
        }

        var inputs = UnicodeInputBuilder.BuildTextInput(chunk);
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }

    private InjectionResult SendInputFailure()
    {
        var error = Marshal.GetLastWin32Error();
        _logger.LogWarning("SendInput завершился с ошибкой {Error}.", error);

        // ERROR_ACCESS_DENIED при SendInput почти всегда означает UIPI.
        return error == 5
            ? InjectionResult.Fail(InjectionFailureKind.PrivilegeBlocked, "Ввод заблокирован системой: окно запущено с более высокими правами.")
            : InjectionResult.Fail(InjectionFailureKind.Unknown, $"Не удалось отправить ввод (код {error}).");
    }

    private async Task<InjectionResult> InjectViaClipboardAsync(string text, CancellationToken cancellationToken)
    {
        var snapshot = _clipboard.Capture();

        if (!_clipboard.SetText(text))
        {
            return InjectionResult.Fail(InjectionFailureKind.ClipboardBusy, "Буфер обмена занят другим приложением.");
        }

        var paste = new List<NativeMethods.INPUT>
        {
            UnicodeInputBuilder.CreateVirtualKeyInput(NativeMethods.VK_CONTROL, keyUp: false),
            UnicodeInputBuilder.CreateVirtualKeyInput(NativeMethods.VK_V, keyUp: false),
            UnicodeInputBuilder.CreateVirtualKeyInput(NativeMethods.VK_V, keyUp: true),
            UnicodeInputBuilder.CreateVirtualKeyInput(NativeMethods.VK_CONTROL, keyUp: true),
        };

        var inputs = paste.ToArray();
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            _clipboard.Restore(snapshot, text);
            return SendInputFailure();
        }

        // Приложению нужно время, чтобы забрать данные из буфера; вернуть
        // прежнее содержимое раньше — значит вставить чужой текст.
        await DelayAsync(_settings.Current.Injection.ClipboardRestoreDelayMs, cancellationToken).ConfigureAwait(false);
        _clipboard.Restore(snapshot, text);

        return InjectionResult.Ok(text);
    }

    private static Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, cancellationToken);
}
