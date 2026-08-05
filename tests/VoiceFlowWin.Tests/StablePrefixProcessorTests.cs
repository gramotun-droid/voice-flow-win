using VoiceFlowWin.Core.Text;
using Xunit;

namespace VoiceFlowWin.Tests;

public class StablePrefixProcessorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static StablePrefixProcessor Create(int repeats = 2, int tailWords = 1, int delayMs = 600) =>
        new(new StablePrefixOptions
        {
            RequiredRepeats = repeats,
            VolatileTailWords = tailWords,
            StabilityDelay = TimeSpan.FromMilliseconds(delayMs),
        });

    [Fact]
    public void Растущая_гипотеза_не_создаёт_повторов()
    {
        // Сценарий 1 из ТЗ: Vosk наращивает фразу слово за словом.
        var processor = Create();
        var hypotheses = new[]
        {
            "разработать",
            "разработать новую",
            "разработать новую систему",
            "разработать новую систему распознавания",
        };

        var appended = new List<string>();
        var time = Start;
        foreach (var hypothesis in hypotheses)
        {
            var update = processor.Process(hypothesis, time);
            if (update.HasNewStableText)
            {
                appended.Add(update.NewStableText);
            }

            time = time.AddMilliseconds(150);
        }

        var finalUpdate = processor.Finalize("разработать новую систему распознавания", time);
        if (finalUpdate.HasNewStableText)
        {
            appended.Add(finalUpdate.NewStableText);
        }

        // Склеенный поток добавлений должен в точности дать исходную фразу.
        Assert.Equal("разработать новую систему распознавания", string.Join(' ', appended));
        Assert.Equal("разработать новую систему распознавания", processor.StableText);
    }

    [Fact]
    public void Слово_становится_стабильным_после_нужного_числа_повторов()
    {
        var processor = Create(repeats: 3, tailWords: 0, delayMs: 10_000);

        Assert.Equal(string.Empty, processor.Process("привет", Start).StableText);
        Assert.Equal(string.Empty, processor.Process("привет", Start.AddMilliseconds(100)).StableText);
        Assert.Equal("привет", processor.Process("привет", Start.AddMilliseconds(200)).StableText);
    }

    [Fact]
    public void Слово_становится_стабильным_по_времени_без_изменений()
    {
        var processor = Create(repeats: 99, tailWords: 0, delayMs: 500);

        Assert.Equal(string.Empty, processor.Process("привет", Start).StableText);
        Assert.Equal("привет", processor.Process("привет", Start.AddMilliseconds(600)).StableText);
    }

    [Fact]
    public void Изменяемый_хвост_не_уходит_в_поле()
    {
        var processor = Create(repeats: 1, tailWords: 2);

        var update = processor.Process("нам нужно разработать новый система", Start);

        Assert.Equal("нам нужно разработать", update.StableText);
        Assert.Equal("новый система", update.VolatileTail);
        Assert.Equal("нам нужно разработать новый система", update.FullText);
    }

    [Fact]
    public void Переписанный_хвост_не_ломает_подтверждённый_префикс()
    {
        // Vosk свободно меняет последние слова: «новый система» → «новую систему».
        var processor = Create(repeats: 1, tailWords: 2);

        processor.Process("нам нужно разработать новый система", Start);
        var update = processor.Process("нам нужно разработать новую систему", Start.AddMilliseconds(150));

        Assert.Equal("нам нужно разработать", update.StableText);
        Assert.Equal("новую систему", update.VolatileTail);
        Assert.False(update.HasNewStableText);
    }

    [Fact]
    public void Сокращение_гипотезы_не_отменяет_уже_введённое()
    {
        var processor = Create(repeats: 1, tailWords: 0);

        processor.Process("один два три", Start);
        var update = processor.Process("один", Start.AddMilliseconds(150));

        // Три слова уже видит пользователь — префикс не сокращается.
        Assert.Equal("один два три", update.StableText);
        Assert.False(update.HasNewStableText);
    }

    [Fact]
    public void Finalize_добавляет_хвост_целиком()
    {
        var processor = Create(repeats: 1, tailWords: 2);

        processor.Process("нам нужно разработать новую систему", Start);
        var update = processor.Finalize("нам нужно разработать новую систему", Start.AddMilliseconds(300));

        Assert.Equal("нам нужно разработать новую систему", update.StableText);
        Assert.Equal("новую систему", update.NewStableText);
        Assert.Equal(string.Empty, update.VolatileTail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Finalize_с_пустым_результатом_не_теряет_хвост(string? finalText)
    {
        // Vosk часто отдаёт пустой финальный результат: все слова уже пришли
        // промежуточными гипотезами. Хвост при этом виден только в overlay,
        // и потерять его — значит проглотить окончание фразы.
        var processor = Create(repeats: 1, tailWords: 2);

        processor.Process("нам нужно разработать новую систему", Start);
        var update = processor.Finalize(finalText, Start.AddMilliseconds(300));

        Assert.Equal("нам нужно разработать новую систему", update.StableText);
        Assert.Equal("новую систему", update.NewStableText);
        Assert.Equal(string.Empty, update.VolatileTail);
    }

    [Fact]
    public void Finalize_короче_префикса_ничего_не_удаляет()
    {
        var processor = Create(repeats: 1, tailWords: 0);

        processor.Process("один два три", Start);
        var update = processor.Finalize("один", Start.AddMilliseconds(300));

        Assert.Equal("один два три", update.StableText);
        Assert.Equal(string.Empty, update.NewStableText);
    }

    [Fact]
    public void Reset_очищает_состояние()
    {
        var processor = Create(repeats: 1, tailWords: 0);
        processor.Process("что-то", Start);
        processor.NotifyInjected("что-то");

        processor.Reset();

        Assert.Equal(string.Empty, processor.StableText);
        Assert.Equal(0, processor.InjectedElementCount);
        Assert.Empty(processor.Hypotheses);
    }

    [Fact]
    public void Учитывает_число_вставленных_текстовых_элементов()
    {
        var processor = Create();

        // Emoji — два char, но один текстовый элемент и один Backspace.
        processor.NotifyInjected("привет 👋");

        Assert.Equal(8, processor.InjectedElementCount);
    }
}
