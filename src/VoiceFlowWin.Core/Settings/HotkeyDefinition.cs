using System.Text;
using System.Text.Json.Serialization;

namespace VoiceFlowWin.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// Горячая клавиша в виде virtual key code и модификаторов.
/// </summary>
/// <remarks>
/// Хранится именно код клавиши, а не символ: иначе при переключении раскладки
/// сочетание «уезжало» бы на другую физическую клавишу. VK_ESCAPE (0x1B)
/// запрещён как основная клавиша — Esc зарезервирован за остановкой диктовки.
/// </remarks>
public sealed record HotkeyDefinition(int VirtualKey, HotkeyModifiers Modifiers)
{
    public const int VirtualKeyEscape = 0x1B;

    /// <summary>Ctrl + Alt + Space.</summary>
    public static readonly HotkeyDefinition Default = new(0x20, HotkeyModifiers.Control | HotkeyModifiers.Alt);

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    /// <summary>Сочетание без модификаторов нельзя зарегистрировать через RegisterHotKey безопасно для печати.</summary>
    [JsonIgnore]
    public bool HasModifiers => Modifiers != HotkeyModifiers.None;

    /// <summary>Esc не может быть основной клавишей вызова.</summary>
    public bool IsAllowedAsPrimary() => VirtualKey != 0 && VirtualKey != VirtualKeyEscape;

    public string ToDisplayString()
    {
        if (IsEmpty)
        {
            return "не назначено";
        }

        var builder = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            builder.Append("Ctrl + ");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt + ");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append("Shift + ");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            builder.Append("Win + ");
        }

        builder.Append(VirtualKeyNames.Describe(VirtualKey));
        return builder.ToString();
    }
}

/// <summary>Человекочитаемые названия клавиш для интерфейса настроек.</summary>
public static class VirtualKeyNames
{
    private static readonly Dictionary<int, string> Named = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "Caps Lock",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Влево",
        [0x26] = "Вверх",
        [0x27] = "Вправо",
        [0x28] = "Вниз",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x5B] = "Left Win",
        [0x5C] = "Right Win",
        [0x5D] = "Menu",
        [0x90] = "Num Lock",
        [0x91] = "Scroll Lock",
        [0xA0] = "Left Shift",
        [0xA1] = "Right Shift",
        [0xA2] = "Left Ctrl",
        [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt",
        [0xA5] = "Right Alt",
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    public static string Describe(int virtualKey)
    {
        if (Named.TryGetValue(virtualKey, out var name))
        {
            return name;
        }

        // F1–F24
        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        // Цифры и буквы основного ряда совпадают с ASCII.
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        // Numpad 0–9
        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return "Num " + (virtualKey - 0x60);
        }

        return "0x" + virtualKey.ToString("X2");
    }
}
