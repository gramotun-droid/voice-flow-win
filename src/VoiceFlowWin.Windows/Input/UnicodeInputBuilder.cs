using VoiceFlowWin.Windows.Interop;

namespace VoiceFlowWin.Windows.Input;

/// <summary>
/// Собирает массив INPUT для Unicode-ввода строки.
/// </summary>
/// <remarks>
/// KEYEVENTF_UNICODE отправляет символ по коду, минуя раскладку — именно
/// поэтому кириллица вводится и при английской раскладке, а пользователю не
/// нужно ничего переключать. Каждый UTF-16 code unit идёт отдельным событием,
/// включая обе половины суррогатной пары: Windows соберёт их обратно.
/// Логика вынесена из сервиса, чтобы её можно было проверить тестом.
/// </remarks>
internal static class UnicodeInputBuilder
{
    /// <summary>Строит пары «нажатие + отпускание» для каждого code unit.</summary>
    internal static NativeMethods.INPUT[] BuildTextInput(string text)
    {
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);

        for (var index = 0; index < text.Length; index++)
        {
            var codeUnit = text[index];
            if (IsNewLine(codeUnit))
            {
                // CRLF — один перенос, а не два Enter подряд.
                if (codeUnit == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                inputs.Add(CreateVirtualKeyInput(NativeMethods.VK_RETURN, keyUp: false));
                inputs.Add(CreateVirtualKeyInput(NativeMethods.VK_RETURN, keyUp: true));
                continue;
            }

            inputs.Add(CreateUnicodeInput(codeUnit, keyUp: false));
            inputs.Add(CreateUnicodeInput(codeUnit, keyUp: true));
        }

        return inputs.ToArray();
    }

    /// <summary>Строит нужное число нажатий Backspace.</summary>
    internal static NativeMethods.INPUT[] BuildBackspaceInput(int count)
    {
        var inputs = new NativeMethods.INPUT[count * 2];
        var index = 0;

        for (var i = 0; i < count; i++)
        {
            inputs[index++] = CreateVirtualKeyInput(NativeMethods.VK_BACK, keyUp: false);
            inputs[index++] = CreateVirtualKeyInput(NativeMethods.VK_BACK, keyUp: true);
        }

        return inputs;
    }

    internal static NativeMethods.INPUT CreateUnicodeInput(char codeUnit, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = codeUnit,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = InputMarker.Value,
            },
        },
    };

    internal static NativeMethods.INPUT CreateVirtualKeyInput(int virtualKey, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                wScan = 0,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = InputMarker.Value,
            },
        },
    };

    /// <summary>Перевод строки надёжнее вводить как Enter, а не как символ.</summary>
    internal static bool IsNewLine(char c) => c is '\n' or '\r';
}

/// <summary>
/// Метка в dwExtraInfo, по которой хуки отличают собственный ввод приложения
/// от настоящего пользовательского. Без неё монитор вмешательства считал бы
/// каждую вставленную букву действием пользователя и мгновенно замораживал
/// сегмент.
/// </summary>
internal static class InputMarker
{
    internal static readonly nint Value = 0x5646_5701;

    internal static bool IsSelfInput(nint extraInfo) => extraInfo == Value;
}
