using System.Globalization;
using System.Text;

namespace VoiceFlowWin.Core.Text;

/// <summary>
/// Приводит распознанный текст к виду, пригодному для вставки в поле ввода.
/// </summary>
/// <remarks>
/// Потоковая модель отдаёт слова без пунктуации и в нижнем регистре, Whisper — уже с
/// пунктуацией, но иногда с лишними пробелами перед знаками и без учёта того,
/// что текст продолжает предыдущее предложение. Всё это чинится здесь, до
/// вставки, чтобы TextInjectionService работал с готовой строкой.
/// </remarks>
public static class TextNormalizer
{
    private static readonly char[] NoSpaceBefore = { '.', ',', '!', '?', ';', ':', ')', ']', '}', '»', '…', '%' };
    private static readonly char[] NoSpaceAfter = { '(', '[', '{', '«' };
    private static readonly char[] SentenceEnd = { '.', '!', '?', '…' };

    /// <summary>Схлопывает пробелы и убирает пробелы перед знаками препинания.</summary>
    public static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Обычные пробелы сами по себе текста не образуют, но перевод строки
        // является осмысленной голосовой командой и должен сохраниться.
        if (!text.Any(c => c is '\n' or '\r') && string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        var trimmed = text.Trim(' ', '\t', '\f', '\v');

        for (var index = 0; index < trimmed.Length; index++)
        {
            var c = trimmed[index];
            if (char.IsWhiteSpace(c))
            {
                // Переводы строк сохраняем: их вставляют голосовые команды.
                if (c is '\n' or '\r')
                {
                    TrimTrailingSpaces(builder);
                    builder.Append('\n');
                    pendingSpace = false;

                    // Windows-перенос CRLF представляет одну новую строку.
                    if (c == '\r' && index + 1 < trimmed.Length && trimmed[index + 1] == '\n')
                    {
                        index++;
                    }

                    continue;
                }

                // После перевода строки пробел не нужен: он сдвинул бы начало
                // новой строки на один символ вправо.
                pendingSpace = builder.Length > 0 && builder[^1] != '\n';
                continue;
            }

            if (pendingSpace && Array.IndexOf(NoSpaceBefore, c) < 0 && !EndsWithNoSpaceAfter(builder))
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Делает первую букву заглавной, не трогая остальной регистр.</summary>
    public static string CapitalizeFirstLetter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetter(text[i]))
            {
                if (char.IsUpper(text[i]))
                {
                    return text;
                }

                return string.Concat(
                    text.AsSpan(0, i),
                    char.ToUpper(text[i], CultureInfo.CurrentCulture).ToString(),
                    text.AsSpan(i + 1));
            }

            if (!char.IsWhiteSpace(text[i]) && text[i] != '«' && text[i] != '"' && text[i] != '(')
            {
                return text;
            }
        }

        return text;
    }

    /// <summary>Ставит завершающую точку, если предложение закончилось без знака.</summary>
    public static string EnsureSentencePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.TrimEnd();
        var last = trimmed[^1];
        if (Array.IndexOf(SentenceEnd, last) >= 0 || last is ',' or ';' or ':' or '-' or '—')
        {
            return trimmed;
        }

        return trimmed + ".";
    }

    /// <summary>
    /// Нужен ли пробел между уже введённым текстом и новым фрагментом.
    /// Пробел не ставится в начале поля и перед знаками препинания.
    /// </summary>
    public static bool NeedsSeparatingSpace(string previousText, string nextText)
    {
        if (string.IsNullOrEmpty(previousText) || string.IsNullOrEmpty(nextText))
        {
            return false;
        }

        var last = previousText[^1];
        if (char.IsWhiteSpace(last) || Array.IndexOf(NoSpaceAfter, last) >= 0)
        {
            return false;
        }

        var first = nextText[0];
        return !char.IsWhiteSpace(first) && Array.IndexOf(NoSpaceBefore, first) < 0;
    }

    /// <summary>
    /// Начинает ли новый фрагмент новое предложение — тогда его первую букву
    /// нужно поднять в верхний регистр.
    /// </summary>
    public static bool StartsNewSentence(string previousText)
    {
        if (string.IsNullOrWhiteSpace(previousText))
        {
            return true;
        }

        var trimmed = previousText.TrimEnd();
        return trimmed.Length == 0 || Array.IndexOf(SentenceEnd, trimmed[^1]) >= 0;
    }

    /// <summary>Полная подготовка финального текста сегмента.</summary>
    public static string PrepareSegmentText(string rawText, string previousContext, bool automaticPunctuation)
    {
        var text = NormalizeWhitespace(rawText);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (StartsNewSentence(previousContext))
        {
            text = CapitalizeFirstLetter(text);
        }

        if (automaticPunctuation)
        {
            text = EnsureSentencePunctuation(text);
        }

        return text;
    }

    private static bool EndsWithNoSpaceAfter(StringBuilder builder) =>
        builder.Length > 0 && Array.IndexOf(NoSpaceAfter, builder[^1]) >= 0;

    private static void TrimTrailingSpaces(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }
    }
}
