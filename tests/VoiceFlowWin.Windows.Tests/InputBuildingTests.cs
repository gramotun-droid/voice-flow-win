using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Windows.Input;
using VoiceFlowWin.Windows.Interop;
using Xunit;

namespace VoiceFlowWin.Windows.Tests;

/// <summary>
/// Проверки Win32-слоя, не требующие реального ввода: правильность структур
/// INPUT и разбор модификаторов. Сами вызовы SendInput здесь не выполняются —
/// они уводили бы фокус и печатали бы в чужие окна на машине сборки.
/// </summary>
public class InputBuildingTests
{
    [Fact]
    public void Каждый_символ_даёт_нажатие_и_отпускание()
    {
        var inputs = UnicodeInputBuilder.BuildTextInput("да");

        Assert.Equal(4, inputs.Length);
        Assert.All(inputs, input => Assert.Equal(NativeMethods.INPUT_KEYBOARD, input.type));
        Assert.Equal(0u, inputs[0].u.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP);
        Assert.NotEqual(0u, inputs[1].u.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void Символ_передаётся_кодом_а_не_виртуальной_клавишей()
    {
        var inputs = UnicodeInputBuilder.BuildTextInput("ж");

        // Именно это делает ввод независимым от текущей раскладки.
        Assert.Equal(0, inputs[0].u.ki.wVk);
        Assert.Equal('ж', (char)inputs[0].u.ki.wScan);
        Assert.NotEqual(0u, inputs[0].u.ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE);
    }

    [Fact]
    public void Суррогатная_пара_отправляется_двумя_событиями()
    {
        // Emoji занимает два UTF-16 code unit — Windows соберёт их обратно.
        var inputs = UnicodeInputBuilder.BuildTextInput("👋");

        Assert.Equal(4, inputs.Length);
        Assert.Equal(0xD83D, inputs[0].u.ki.wScan);
        Assert.Equal(0xDC4B, inputs[2].u.ki.wScan);
    }

    [Fact]
    public void Backspace_строится_нужное_число_раз()
    {
        var inputs = UnicodeInputBuilder.BuildBackspaceInput(3);

        Assert.Equal(6, inputs.Length);
        Assert.All(inputs, input => Assert.Equal(NativeMethods.VK_BACK, input.u.ki.wVk));
    }

    [Fact]
    public void Собственный_ввод_помечается_меткой()
    {
        var inputs = UnicodeInputBuilder.BuildTextInput("а");

        // Без метки монитор вмешательства принял бы наш ввод за пользовательский.
        Assert.True(InputMarker.IsSelfInput(inputs[0].u.ki.dwExtraInfo));
        Assert.False(InputMarker.IsSelfInput(0));
    }

    [Fact]
    public void Модификаторы_переводятся_в_флаги_RegisterHotKey()
    {
        var flags = GlobalHotkeyService.ToNativeModifiers(HotkeyModifiers.Control | HotkeyModifiers.Alt);

        Assert.Equal(NativeMethods.MOD_CONTROL, flags & NativeMethods.MOD_CONTROL);
        Assert.Equal(NativeMethods.MOD_ALT, flags & NativeMethods.MOD_ALT);
        Assert.Equal(0u, flags & NativeMethods.MOD_SHIFT);

        // MOD_NOREPEAT обязателен: иначе удержание сочетания сыплет событиями.
        Assert.Equal(NativeMethods.MOD_NOREPEAT, flags & NativeMethods.MOD_NOREPEAT);
    }

    [Fact]
    public void Пустая_строка_не_создаёт_событий() =>
        Assert.Empty(UnicodeInputBuilder.BuildTextInput(string.Empty));
}

public class HotkeyDefinitionTests
{
    [Fact]
    public void Esc_не_может_быть_клавишей_вызова()
    {
        var escape = new HotkeyDefinition(HotkeyDefinition.VirtualKeyEscape, HotkeyModifiers.None);

        Assert.False(escape.IsAllowedAsPrimary());
        Assert.True(HotkeyDefinition.Default.IsAllowedAsPrimary());
    }

    [Fact]
    public void Сочетание_по_умолчанию_это_Ctrl_F5() =>
        Assert.Equal("Ctrl + F5", HotkeyDefinition.Default.ToDisplayString());

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x41, "A")]
    [InlineData(0x60, "Num 0")]
    public void Клавиши_показываются_человекочитаемо(int virtualKey, string expected) =>
        Assert.Equal(expected, VirtualKeyNames.Describe(virtualKey));

    [Fact]
    public void Хранится_код_клавиши_а_не_символ()
    {
        // Один и тот же код на русской и английской раскладке даёт разные
        // символы, поэтому сочетание не должно зависеть от раскладки.
        var hotkey = new HotkeyDefinition(0x41, HotkeyModifiers.Control);

        Assert.Equal(0x41, hotkey.VirtualKey);
        Assert.Equal("Ctrl + A", hotkey.ToDisplayString());
    }
}
