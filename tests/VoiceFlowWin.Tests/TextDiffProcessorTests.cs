using VoiceFlowWin.Core.Text;
using Xunit;

namespace VoiceFlowWin.Tests;

public class TextDiffProcessorTests
{
    [Fact]
    public void Одинаковый_текст_не_требует_правки()
    {
        var plan = TextDiffProcessor.ComputeTailReplacement("Привет", "Привет", maxBackspaces: 6);

        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public void Правка_хвоста_удаляет_только_расходящуюся_часть()
    {
        var plan = TextDiffProcessor.ComputeTailReplacement(
            "нам нужно разработать новый система",
            "Нам нужно разработать новую систему.",
            maxBackspaces: 35);

        // Различие начинается с первой буквы: «н» против «Н».
        Assert.Equal(35, plan.BackspaceCount);
        Assert.Equal("Нам нужно разработать новую систему.", plan.TextToType);
    }

    [Fact]
    public void Совпадающий_префикс_сохраняется()
    {
        var plan = TextDiffProcessor.ComputeTailReplacement(
            "Нам нужно разработать новый система",
            "Нам нужно разработать новую систему.",
            maxBackspaces: 35);

        // Совпадает даже начало последнего слова — «нов», поэтому стирается
        // только «ый система», а не всё словосочетание целиком.
        Assert.Equal(10, plan.BackspaceCount);
        Assert.Equal("ую систему.", plan.TextToType);
    }

    [Fact]
    public void Дописывание_в_конец_не_требует_удаления()
    {
        var plan = TextDiffProcessor.ComputeTailReplacement("Привет", "Привет, мир", maxBackspaces: 6);

        Assert.Equal(0, plan.BackspaceCount);
        Assert.Equal(", мир", plan.TextToType);
    }

    [Fact]
    public void Правка_за_пределами_собственного_текста_запрещена()
    {
        // Приложение вставило 5 символов, а правка требует стереть 5 —
        // но лимит разрешает только 3, значит трогать поле нельзя.
        var plan = TextDiffProcessor.ComputeTailReplacement("текст", "другой", maxBackspaces: 3);

        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public void Backspace_считается_в_текстовых_элементах_а_не_в_char()
    {
        // «👋» — суррогатная пара: два char, один Backspace.
        var plan = TextDiffProcessor.ComputeTailReplacement("привет 👋", "привет", maxBackspaces: 8);

        Assert.Equal(2, plan.BackspaceCount);
        Assert.Equal(string.Empty, plan.TextToType);
    }

    [Fact]
    public void Расстояние_и_похожесть_считаются_корректно()
    {
        Assert.Equal(0, TextDiffProcessor.EditDistance("тест", "тест"));
        Assert.Equal(1, TextDiffProcessor.EditDistance("тест", "тесты"));
        Assert.Equal(1.0, TextDiffProcessor.Similarity("тест", "тест"));
        Assert.True(TextDiffProcessor.Similarity("новый система", "новую систему") > 0.7);
        Assert.True(TextDiffProcessor.Similarity("привет как дела", "совершенно другой текст") < 0.35);
    }

    [Fact]
    public void Пословный_diff_показывает_изменения()
    {
        var diff = TextDiffProcessor.WordDiff("нам нужно новый система", "нам нужно новую систему");

        Assert.Equal(2, diff.Count(op => op.Kind == WordDiffKind.Equal));
        Assert.Equal(2, diff.Count(op => op.Kind == WordDiffKind.Removed));
        Assert.Equal(2, diff.Count(op => op.Kind == WordDiffKind.Added));
    }

    [Fact]
    public void Общий_пословный_префикс_находится()
    {
        Assert.Equal(3, TextDiffProcessor.CommonWordPrefix("один два три четыре", "один два три пять"));
        Assert.Equal(0, TextDiffProcessor.CommonWordPrefix("раз", "два"));
    }
}
