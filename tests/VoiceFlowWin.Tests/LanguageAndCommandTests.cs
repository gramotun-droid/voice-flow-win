using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Commands;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Core.Text;
using VoiceFlowWin.Tests.Fakes;
using Xunit;

namespace VoiceFlowWin.Tests;

public class LanguageSelectionTests
{
    private const int RussianLayout = 0x04190419;
    private const int EnglishLayout = 0x04090409;

    [Fact]
    public void Раскладка_определяет_язык()
    {
        Assert.Equal(RecognitionLanguage.Russian, RecognitionLanguageExtensions.FromLayoutId(RussianLayout));
        Assert.Equal(RecognitionLanguage.English, RecognitionLanguageExtensions.FromLayoutId(EnglishLayout));
    }

    [Fact]
    public void Снимок_фокуса_отдаёт_язык_раскладки()
    {
        var focus = WindowFocusSnapshot.Unknown with { KeyboardLayoutId = RussianLayout };

        Assert.Equal(RecognitionLanguage.Russian, focus.LayoutLanguage);
    }

    [Fact]
    public void Смена_раскладки_не_считается_вмешательством()
    {
        // Пользователь может переключить язык, не трогая текст, — уже начатый
        // сегмент от этого не должен ломаться.
        var baseline = WindowFocusSnapshot.Unknown with { WindowHandle = 1, KeyboardLayoutId = RussianLayout };
        var afterSwitch = baseline with { KeyboardLayoutId = EnglishLayout };

        Assert.True(baseline.IsSameTarget(afterSwitch));
    }

    [Fact]
    public void Смена_окна_считается_другой_целью()
    {
        var baseline = WindowFocusSnapshot.Unknown with { WindowHandle = 1, FocusedControlHandle = 2 };

        Assert.False(baseline.IsSameTarget(baseline with { WindowHandle = 9 }));
        Assert.False(baseline.IsSameTarget(baseline with { FocusedControlHandle = 9 }));
    }

    [Fact]
    public void Неизвестная_позиция_каретки_не_считается_перемещением()
    {
        var baseline = WindowFocusSnapshot.Unknown with { CaretPosition = -1 };

        Assert.True(baseline.IsSameCaret(baseline with { CaretPosition = 100 }));
    }

    [Theory]
    [InlineData(LanguageSelectionMode.AlwaysRussian, EnglishLayout, RecognitionLanguage.Russian)]
    [InlineData(LanguageSelectionMode.AlwaysEnglish, RussianLayout, RecognitionLanguage.English)]
    [InlineData(LanguageSelectionMode.WhisperAutoDetect, RussianLayout, RecognitionLanguage.Auto)]
    [InlineData(LanguageSelectionMode.FollowKeyboardLayout, RussianLayout, RecognitionLanguage.Russian)]
    [InlineData(LanguageSelectionMode.FollowKeyboardLayout, EnglishLayout, RecognitionLanguage.English)]
    public void Режим_выбора_языка_учитывается(LanguageSelectionMode mode, int layout, RecognitionLanguage expected)
    {
        var settings = new FakeSettingsService();
        settings.Mutate(current => current.General.LanguageMode = mode);
        var focus = WindowFocusSnapshot.Unknown with { KeyboardLayoutId = layout };

        var language = mode switch
        {
            LanguageSelectionMode.AlwaysRussian => RecognitionLanguage.Russian,
            LanguageSelectionMode.AlwaysEnglish => RecognitionLanguage.English,
            LanguageSelectionMode.WhisperAutoDetect => RecognitionLanguage.Auto,
            _ => focus.LayoutLanguage,
        };

        Assert.Equal(expected, language);
    }

    [Fact]
    public async Task Новый_сегмент_берёт_текущую_раскладку()
    {
        var field = new FakeTextField();
        var tracker = new FakeFocusTracker();
        var monitor = new FakeInterventionMonitor(tracker);
        var settings = new FakeSettingsService();
        var coordinator = new HybridTranscriptionCoordinator(
            new FakeInjectionService(field),
            tracker,
            monitor,
            new DictionaryProcessor(),
            new VoiceCommandProcessor(),
            settings);

        var first = coordinator.BeginSegment(tracker.Capture().LayoutLanguage);
        await coordinator.EndSegmentAsync(first.SegmentId, "тест", Array.Empty<byte>(), CancellationToken.None);

        tracker.SwitchLayout(EnglishLayout);
        var second = coordinator.BeginSegment(tracker.Capture().LayoutLanguage);

        // Уже закрытый сегмент язык не меняет, новый берёт актуальную раскладку.
        Assert.Equal(RecognitionLanguage.Russian, first.Language);
        Assert.Equal(RecognitionLanguage.English, second.Language);
    }
}

public class VoiceCommandProcessorTests
{
    private static readonly VoiceCommandProcessor Processor = new();

    [Theory]
    [InlineData("привет точка", "привет.")]
    [InlineData("да запятая нет", "да, нет")]
    [InlineData("вопрос вопросительный знак", "вопрос?")]
    [InlineData("итог двоеточие первое", "итог: первое")]
    [InlineData("раз точка с запятой два", "раз; два")]
    public void Команды_пунктуации_раскрываются(string input, string expected) =>
        Assert.Equal(expected, Processor.Process(input, RecognitionLanguage.Russian).Text);

    [Fact]
    public void Новая_строка_вставляет_перенос() =>
        Assert.Equal("первая\nвторая", Processor.Process("первая новая строка вторая", RecognitionLanguage.Russian).Text);

    [Fact]
    public void Удаление_последнего_слова_убирает_слово() =>
        Assert.Equal("один два", Processor.Process("один два три удалить последнее слово", RecognitionLanguage.Russian).Text);

    [Fact]
    public void Отмена_фразы_очищает_текст()
    {
        var result = Processor.Process("какой-то текст отменить фразу", RecognitionLanguage.Russian);

        Assert.True(result.CancelRequested);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void Обычная_речь_не_превращается_в_команды()
    {
        // «поставь точку в конце» — это фраза, а не команда «точка».
        var result = Processor.Process("поставь точку в конце", RecognitionLanguage.Russian);

        Assert.Equal("поставь точку в конце", result.Text);
    }

    [Fact]
    public void Длинная_команда_выигрывает_у_короткой() =>
        Assert.Equal("раз; два", Processor.Process("раз точка с запятой два", RecognitionLanguage.Russian).Text);

    [Fact]
    public void Выключенная_обработка_оставляет_текст_как_есть()
    {
        var processor = new VoiceCommandProcessor { Enabled = false };

        Assert.Equal("привет точка", processor.Process("привет точка", RecognitionLanguage.Russian).Text);
    }

    [Fact]
    public void Команды_чужого_языка_не_срабатывают() =>
        Assert.Equal("привет comma мир", Processor.Process("привет comma мир", RecognitionLanguage.Russian).Text);

    [Fact]
    public void Русские_команды_работают_в_сегменте_с_английским_языком() =>
        // Язык сегмента берётся из раскладки активного окна, а диктовать
        // по-русски в окне с английской раскладкой — обычное дело.
        Assert.Equal("привет, мир.", Processor.Process("привет запятая мир точка", RecognitionLanguage.English).Text);

    [Fact]
    public void Русские_команды_работают_при_автоопределении_языка() =>
        Assert.Equal("раз два.", Processor.Process("раз два точка", RecognitionLanguage.Auto).Text);
}

public class TextNormalizerTests
{
    [Fact]
    public void Пробелы_перед_знаками_убираются() =>
        Assert.Equal("Привет, мир!", TextNormalizer.NormalizeWhitespace("Привет ,   мир !"));

    [Fact]
    public void Переносы_строк_сохраняются() =>
        Assert.Equal("первая\nвторая", TextNormalizer.NormalizeWhitespace("первая \n вторая"));

    [Fact]
    public void Первая_буква_поднимается_в_верхний_регистр()
    {
        Assert.Equal("Привет", TextNormalizer.CapitalizeFirstLetter("привет"));
        Assert.Equal("GitHub", TextNormalizer.CapitalizeFirstLetter("GitHub"));
        Assert.Equal("«Цитата»", TextNormalizer.CapitalizeFirstLetter("«цитата»"));
    }

    [Fact]
    public void Точка_добавляется_только_при_её_отсутствии()
    {
        Assert.Equal("Готово.", TextNormalizer.EnsureSentencePunctuation("Готово"));
        Assert.Equal("Готово!", TextNormalizer.EnsureSentencePunctuation("Готово!"));
        Assert.Equal("Готово,", TextNormalizer.EnsureSentencePunctuation("Готово,"));
    }

    [Fact]
    public void Разделяющий_пробел_ставится_осмысленно()
    {
        Assert.True(TextNormalizer.NeedsSeparatingSpace("Первая фраза.", "Вторая"));
        Assert.False(TextNormalizer.NeedsSeparatingSpace("Первая фраза ", "вторая"));
        Assert.False(TextNormalizer.NeedsSeparatingSpace("Первая фраза", ", продолжение"));
        Assert.False(TextNormalizer.NeedsSeparatingSpace(string.Empty, "Первая"));
    }

    [Fact]
    public void Новое_предложение_определяется_по_предыдущему_тексту()
    {
        Assert.True(TextNormalizer.StartsNewSentence("Фраза."));
        Assert.True(TextNormalizer.StartsNewSentence(string.Empty));
        Assert.False(TextNormalizer.StartsNewSentence("Фраза без точки"));
    }

    [Fact]
    public void Полная_подготовка_сегмента() =>
        Assert.Equal(
            "Нам нужно разработать новую систему.",
            TextNormalizer.PrepareSegmentText("нам нужно  разработать новую систему", string.Empty, automaticPunctuation: true));
}
