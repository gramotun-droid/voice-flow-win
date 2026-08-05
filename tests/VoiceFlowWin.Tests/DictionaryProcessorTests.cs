using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using Xunit;

namespace VoiceFlowWin.Tests;

public class DictionaryProcessorTests
{
    private static DictionaryProcessor CreateDefault() => new(DefaultDictionary.Create());

    [Theory]
    [InlineData("открой гитхаб", "открой GitHub")]
    [InlineData("создай пул реквест", "создай pull request")]
    [InlineData("проект на дот нет", "проект на .NET")]
    [InlineData("пишем на си шарп", "пишем на C#")]
    [InlineData("добавь джейсон файл", "добавь JSON файл")]
    [InlineData("запусти докер компоуз", "запусти Docker Compose")]
    [InlineData("открой визуал студио код", "открой Visual Studio Code")]
    [InlineData("используй виспер", "используй Whisper")]
    public void Заменяет_известные_термины(string input, string expected)
    {
        var processor = CreateDefault();

        Assert.Equal(expected, processor.Apply(input, RecognitionLanguage.Russian));
    }

    [Fact]
    public void Не_заменяет_совпадение_внутри_другого_слова()
    {
        var processor = CreateDefault();

        // «воск» → Vosk, но «воскресенье» должно остаться словом.
        Assert.Equal("в воскресенье отдыхаем", processor.Apply("в воскресенье отдыхаем", RecognitionLanguage.Russian));
        Assert.Equal("используй Vosk", processor.Apply("используй воск", RecognitionLanguage.Russian));
    }

    [Fact]
    public void Сохраняет_пунктуацию_вокруг_замены()
    {
        var processor = CreateDefault();

        Assert.Equal("открой GitHub, потом C#.", processor.Apply("открой гитхаб, потом си шарп.", RecognitionLanguage.Russian));
    }

    [Fact]
    public void Не_склеивает_термин_через_запятую()
    {
        var processor = CreateDefault();

        // «докер, компоуз» — это перечисление, а не «Docker Compose».
        Assert.DoesNotContain("Docker Compose", processor.Apply("докер, компоуз", RecognitionLanguage.Russian));
    }

    [Fact]
    public void Длинная_фраза_выигрывает_у_короткой()
    {
        var processor = new DictionaryProcessor(new[]
        {
            new UserDictionaryEntry { SpokenForm = "докер", Replacement = "Docker" },
            new UserDictionaryEntry { SpokenForm = "докер компоуз", Replacement = "Docker Compose" },
        });

        Assert.Equal("Docker Compose", processor.Apply("докер компоуз", RecognitionLanguage.Auto));
        Assert.Equal("Docker", processor.Apply("докер", RecognitionLanguage.Auto));
    }

    [Fact]
    public void Отключённая_запись_не_применяется()
    {
        var processor = new DictionaryProcessor(new[]
        {
            new UserDictionaryEntry { SpokenForm = "гитхаб", Replacement = "GitHub", Enabled = false },
        });

        Assert.Equal("гитхаб", processor.Apply("гитхаб", RecognitionLanguage.Auto));
    }

    [Fact]
    public void Варианты_произношения_работают()
    {
        var processor = CreateDefault();

        Assert.Equal(".NET", processor.Apply("дотнет", RecognitionLanguage.Russian));
        Assert.Equal(".NET", processor.Apply("точка нет", RecognitionLanguage.Russian));
    }

    [Fact]
    public void Ё_и_е_считаются_одинаковыми()
    {
        var processor = new DictionaryProcessor(new[]
        {
            new UserDictionaryEntry { SpokenForm = "ёлка", Replacement = "Ель" },
        });

        Assert.Equal("Ель", processor.Apply("елка", RecognitionLanguage.Auto));
        Assert.Equal("Ель", processor.Apply("ёлка", RecognitionLanguage.Auto));
    }

    [Fact]
    public void Запись_другого_языка_не_применяется()
    {
        var processor = new DictionaryProcessor(new[]
        {
            new UserDictionaryEntry { SpokenForm = "гитхаб", Replacement = "GitHub", Language = RecognitionLanguage.Russian },
        });

        Assert.Equal("гитхаб", processor.Apply("гитхаб", RecognitionLanguage.English));
        Assert.Equal("GitHub", processor.Apply("гитхаб", RecognitionLanguage.Russian));
    }

    [Fact]
    public void Пустой_словарь_возвращает_текст_без_изменений()
    {
        var processor = new DictionaryProcessor();

        Assert.Equal("любой текст", processor.Apply("любой текст", RecognitionLanguage.Auto));
    }
}
