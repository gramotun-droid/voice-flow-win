using System.Text;
using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.Core.Commands;

/// <summary>Действие, которое команда выполняет над уже введённым текстом.</summary>
public enum VoiceCommandEffect
{
    /// <summary>Вставить символ или перевод строки.</summary>
    Insert,

    /// <summary>Удалить последнее слово текущего сегмента.</summary>
    DeleteLastWord,

    /// <summary>Отменить весь текущий сегмент.</summary>
    CancelPhrase,
}

public sealed record VoiceCommand(string[] Tokens, VoiceCommandEffect Effect, string Insertion, RecognitionLanguage Language);

/// <param name="Text">Текст после раскрытия команд.</param>
/// <param name="DeleteWordRequests">Сколько раз прозвучало «удалить последнее слово» уже на пустом тексте.</param>
/// <param name="CancelRequested">Прозвучало «отменить фразу».</param>
public readonly record struct VoiceCommandResult(string Text, int DeleteWordRequests, bool CancelRequested)
{
    public static VoiceCommandResult Unchanged(string text) => new(text, 0, false);
}

/// <summary>
/// Раскрывает голосовые команды пунктуации и правки.
/// </summary>
/// <remarks>
/// Команда срабатывает только при точном совпадении всей фразы целиком:
/// «поставь точку в конце» останется текстом, а отдельно произнесённое
/// «точка» превратится в «.». Иначе обычная речь начала бы разваливаться на
/// знаки препинания. Обработка целиком отключается одной настройкой.
/// </remarks>
public sealed class VoiceCommandProcessor
{
    private static readonly VoiceCommand[] Commands =
    {
        Insert("точка", ".", RecognitionLanguage.Russian),
        Insert("запятая", ",", RecognitionLanguage.Russian),
        Insert("двоеточие", ":", RecognitionLanguage.Russian),
        Insert("точка с запятой", ";", RecognitionLanguage.Russian),
        Insert("точка запятой", ";", RecognitionLanguage.Russian),
        Insert("вопросительный знак", "?", RecognitionLanguage.Russian),
        Insert("восклицательный знак", "!", RecognitionLanguage.Russian),
        Insert("многоточие", "…", RecognitionLanguage.Russian),
        Insert("тире", "—", RecognitionLanguage.Russian),
        Insert("с новой строки", "\n", RecognitionLanguage.Russian),
        Insert("новая строка", "\n", RecognitionLanguage.Russian),
        Insert("новый абзац", "\n\n", RecognitionLanguage.Russian),
        Insert("открыть скобку", "(", RecognitionLanguage.Russian),
        Insert("закрыть скобку", ")", RecognitionLanguage.Russian),
        Insert("кавычки", "«»", RecognitionLanguage.Russian),
        new(new[] { "удалить", "последнее", "слово" }, VoiceCommandEffect.DeleteLastWord, string.Empty, RecognitionLanguage.Russian),
        new(new[] { "отменить", "фразу" }, VoiceCommandEffect.CancelPhrase, string.Empty, RecognitionLanguage.Russian),

        Insert("period", ".", RecognitionLanguage.English),
        Insert("full stop", ".", RecognitionLanguage.English),
        Insert("comma", ",", RecognitionLanguage.English),
        Insert("colon", ":", RecognitionLanguage.English),
        Insert("semicolon", ";", RecognitionLanguage.English),
        Insert("question mark", "?", RecognitionLanguage.English),
        Insert("exclamation mark", "!", RecognitionLanguage.English),
        Insert("new line", "\n", RecognitionLanguage.English),
        Insert("new paragraph", "\n\n", RecognitionLanguage.English),
        Insert("open bracket", "(", RecognitionLanguage.English),
        Insert("close bracket", ")", RecognitionLanguage.English),
        new(new[] { "delete", "last", "word" }, VoiceCommandEffect.DeleteLastWord, string.Empty, RecognitionLanguage.English),
        new(new[] { "cancel", "phrase" }, VoiceCommandEffect.CancelPhrase, string.Empty, RecognitionLanguage.English),
    };

    private readonly int _maxTokens = Commands.Max(command => command.Tokens.Length);

    public bool Enabled { get; set; } = true;

    public VoiceCommandResult Process(string? text, RecognitionLanguage language)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
        {
            return VoiceCommandResult.Unchanged(text ?? string.Empty);
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var output = new List<string>(tokens.Length);
        var deleteRequests = 0;
        var cancelRequested = false;

        var index = 0;
        while (index < tokens.Length)
        {
            var command = MatchAt(tokens, index, language, out var length);
            if (command is null)
            {
                output.Add(tokens[index]);
                index++;
                continue;
            }

            switch (command.Effect)
            {
                case VoiceCommandEffect.Insert:
                    AppendInsertion(output, command.Insertion);
                    break;

                case VoiceCommandEffect.DeleteLastWord:
                    if (output.Count > 0)
                    {
                        output.RemoveAt(output.Count - 1);
                    }
                    else
                    {
                        // Слова этого сегмента кончились — удалять придётся уже
                        // введённый текст, этим займётся вызывающая сторона.
                        deleteRequests++;
                    }

                    break;

                case VoiceCommandEffect.CancelPhrase:
                    output.Clear();
                    deleteRequests = 0;
                    cancelRequested = true;
                    break;
            }

            index += length;
        }

        return new VoiceCommandResult(Join(output), deleteRequests, cancelRequested);
    }

    /// <summary>Действует ли команда в сегменте с таким языком.</summary>
    /// <remarks>
    /// Русские команды принимаются в любом сегменте. Язык сегмента берётся из
    /// раскладки активного окна, а диктовать по-русски в окне с английской
    /// раскладкой — обычное дело: раньше «точка» и «запятая» там просто не
    /// срабатывали. Кириллическое слово английская модель выдать не может, так
    /// что спутать его с речью нельзя.
    ///
    /// Английские команды остаются привязанными к языку: «period» и «comma» —
    /// обычные слова, а английские термины внутри русской речи приложение
    /// сохраняет намеренно.
    /// </remarks>
    private static bool AppliesTo(VoiceCommand command, RecognitionLanguage language) =>
        command.Language == RecognitionLanguage.Russian ||
        language == RecognitionLanguage.Auto ||
        command.Language == language;

    private VoiceCommand? MatchAt(string[] tokens, int start, RecognitionLanguage language, out int matchedLength)
    {
        var available = Math.Min(_maxTokens, tokens.Length - start);

        // Длинные команды проверяются первыми: «точка с запятой» важнее «точка».
        for (var length = available; length >= 1; length--)
        {
            foreach (var command in Commands)
            {
                if (command.Tokens.Length != length)
                {
                    continue;
                }

                if (!AppliesTo(command, language))
                {
                    continue;
                }

                var matches = true;
                for (var i = 0; i < length; i++)
                {
                    var token = tokens[start + i].Trim(Dictionary.WordSpan.PunctuationChars).ToLowerInvariant().Replace('ё', 'е');
                    if (!string.Equals(token, command.Tokens[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    matchedLength = length;
                    return command;
                }
            }
        }

        matchedLength = 0;
        return null;
    }

    private static void AppendInsertion(List<string> output, string insertion)
    {
        if (insertion is "\n" or "\n\n")
        {
            output.Add(insertion);
            return;
        }

        // Тире — отдельный знак между словами, вокруг него нужны пробелы.
        if (insertion == "—")
        {
            output.Add(insertion);
            return;
        }

        // Знак препинания прилипает к предыдущему слову, а не висит отдельно.
        if (output.Count > 0 && output[^1] is not "\n" and not "\n\n")
        {
            output[^1] += insertion;
        }
        else
        {
            output.Add(insertion);
        }
    }

    private static string Join(List<string> tokens)
    {
        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            if (token is "\n" or "\n\n")
            {
                builder.Append(token);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append(' ');
            }

            builder.Append(token);
        }

        return builder.ToString();
    }

    private static VoiceCommand Insert(string phrase, string insertion, RecognitionLanguage language) =>
        new(phrase.Split(' '), VoiceCommandEffect.Insert, insertion, language);
}
