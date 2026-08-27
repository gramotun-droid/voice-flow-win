using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.Core.Dictionary;

/// <summary>Одна запись пользовательского словаря замен.</summary>
public sealed class UserDictionaryEntry
{
    /// <summary>Как это звучит и как распознаёт потоковая модель: «гитхаб», «пул реквест».</summary>
    public string SpokenForm { get; set; } = string.Empty;

    /// <summary>Как это должно быть записано: <c>GitHub</c>, <c>pull request</c>.</summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>
    /// Учитывать регистр произносимой формы при поиске. По умолчанию false:
    /// распознаватель почти всегда отдаёт текст в нижнем регистре.
    /// </summary>
    public bool CaseSensitive { get; set; }

    /// <summary>Язык, для которого запись применяется. Auto — для любого.</summary>
    public RecognitionLanguage Language { get; set; } = RecognitionLanguage.Auto;

    /// <summary>Допустимые варианты произношения: «дот нет», «дотнет», «точка нет».</summary>
    public List<string> Variants { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public IEnumerable<string> AllForms()
    {
        if (!string.IsNullOrWhiteSpace(SpokenForm))
        {
            yield return SpokenForm;
        }

        foreach (var variant in Variants)
        {
            if (!string.IsNullOrWhiteSpace(variant))
            {
                yield return variant;
            }
        }
    }
}

/// <summary>Словарь по умолчанию: термины, на которых ASR ошибается чаще всего.</summary>
public static class DefaultDictionary
{
    public static List<UserDictionaryEntry> Create() => new()
    {
        Entry("гитхаб", "GitHub", "гит хаб", "гитхап"),
        Entry("пул реквест", "pull request", "пулл реквест", "пул реквесты"),
        Entry("дот нет", ".NET", "дотнет", "точка нет"),
        Entry("си шарп", "C#", "сишарп"),
        Entry("джейсон", "JSON", "джсон"),
        Entry("битрикс двадцать четыре", "Bitrix24", "битрикс 24"),
        Entry("докер компоуз", "Docker Compose", "докер компоус"),
        Entry("визуал студио код", "Visual Studio Code", "вижуал студио код"),
        Entry("виспер", "Whisper"),
        Entry("воск", "Vosk"),
        Entry("линукс", "Linux"),
        Entry("виндовс", "Windows"),
        Entry("эйпиай", "API", "апи"),
        Entry("хтмл", "HTML", "аштиэмэль"),
        Entry("постгрес", "PostgreSQL", "постгрескуэль"),
    };

    private static UserDictionaryEntry Entry(string spoken, string replacement, params string[] variants) => new()
    {
        SpokenForm = spoken,
        Replacement = replacement,
        Variants = variants.ToList(),
        Language = RecognitionLanguage.Russian,
    };
}
