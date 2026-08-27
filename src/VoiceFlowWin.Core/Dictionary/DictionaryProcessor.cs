using System.Text;
using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.Core.Dictionary;

/// <summary>
/// Применяет пользовательский словарь к распознанному тексту.
/// </summary>
/// <remarks>
/// Совпадение только точное и только по границам слов: «воск» превращается в
/// <c>Vosk</c>, но «воскресенье» остаётся нетронутым. Никакого фонетического
/// сходства — иначе обычная русская речь начала бы превращаться в английские
/// термины. Замена выполняется и над промежуточным текстом Zipformer, и над
/// финальным текстом Whisper, всегда до вставки в поле.
/// </remarks>
public sealed class DictionaryProcessor
{
    private sealed record CompiledEntry(string[] Tokens, string Replacement, bool CaseSensitive, RecognitionLanguage Language);

    private readonly List<CompiledEntry> _entries = new();
    private int _maxTokens = 1;

    public DictionaryProcessor(IEnumerable<UserDictionaryEntry>? entries = null)
    {
        Reload(entries ?? Array.Empty<UserDictionaryEntry>());
    }

    public int EntryCount => _entries.Count;

    public void Reload(IEnumerable<UserDictionaryEntry> entries)
    {
        _entries.Clear();
        _maxTokens = 1;

        foreach (var entry in entries)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Replacement))
            {
                continue;
            }

            foreach (var form in entry.AllForms())
            {
                var tokens = Tokenize(form);
                if (tokens.Length == 0)
                {
                    continue;
                }

                _entries.Add(new CompiledEntry(tokens, entry.Replacement, entry.CaseSensitive, entry.Language));
                _maxTokens = Math.Max(_maxTokens, tokens.Length);
            }
        }

        // Длинные фразы должны выигрывать у коротких: «докер компоуз» важнее «докер».
        _entries.Sort((left, right) => right.Tokens.Length.CompareTo(left.Tokens.Length));
    }

    public string Apply(string text, RecognitionLanguage language)
    {
        if (string.IsNullOrWhiteSpace(text) || _entries.Count == 0)
        {
            return text ?? string.Empty;
        }

        var words = WordSpan.SplitPreservingLayout(text);
        if (words.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < words.Count)
        {
            var matched = TryMatchAt(words, index, language, out var replacement, out var length);
            if (matched)
            {
                builder.Append(words[index].Leading);
                builder.Append(replacement);
                builder.Append(words[index + length - 1].Trailing);
                index += length;
                continue;
            }

            var word = words[index];
            builder.Append(word.Leading).Append(word.Core).Append(word.Trailing);
            index++;
        }

        return builder.ToString();
    }

    private bool TryMatchAt(
        IReadOnlyList<WordSpan> words,
        int start,
        RecognitionLanguage language,
        out string replacement,
        out int matchedLength)
    {
        var available = Math.Min(_maxTokens, words.Count - start);

        foreach (var entry in _entries)
        {
            if (entry.Tokens.Length > available)
            {
                continue;
            }

            if (entry.Language != RecognitionLanguage.Auto &&
                language != RecognitionLanguage.Auto &&
                entry.Language != language)
            {
                continue;
            }

            // Внутри фразы не должно быть знаков препинания: «докер, компоуз»
            // это не термин «Docker Compose», а перечисление.
            var mismatch = false;
            for (var i = 0; i < entry.Tokens.Length; i++)
            {
                var word = words[start + i];
                if (i < entry.Tokens.Length - 1 && word.Trailing.Length > 0)
                {
                    mismatch = true;
                    break;
                }

                var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!string.Equals(Normalize(word.Core, entry.CaseSensitive), entry.Tokens[i], comparison))
                {
                    mismatch = true;
                    break;
                }
            }

            if (mismatch)
            {
                continue;
            }

            replacement = entry.Replacement;
            matchedLength = entry.Tokens.Length;
            return true;
        }

        replacement = string.Empty;
        matchedLength = 0;
        return false;
    }

    private static string[] Tokenize(string form) =>
        form.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => Normalize(token.Trim(WordSpan.PunctuationChars), caseSensitive: false))
            .Where(token => token.Length > 0)
            .ToArray();

    /// <summary>«ё» и «е» распознаватель путает постоянно, поэтому сводим их к одному виду.</summary>
    private static string Normalize(string value, bool caseSensitive)
    {
        var normalized = value.Replace('ё', 'е').Replace('Ё', 'Е');
        return caseSensitive ? normalized : normalized.ToLowerInvariant();
    }
}

/// <summary>Слово вместе с окружающими пробелами и знаками препинания.</summary>
public readonly record struct WordSpan(string Leading, string Core, string Trailing)
{
    public static readonly char[] PunctuationChars = ".,!?;:()[]{}«»\"'—–-…".ToCharArray();

    /// <summary>
    /// Разбивает текст так, чтобы его можно было собрать обратно без потерь:
    /// пробелы и пунктуация сохраняются отдельно от самого слова.
    /// </summary>
    public static IReadOnlyList<WordSpan> SplitPreservingLayout(string text)
    {
        var result = new List<WordSpan>();
        var position = 0;

        while (position < text.Length)
        {
            var leadingStart = position;
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || IsPunctuation(text[position])))
            {
                position++;
            }

            var leading = text[leadingStart..position];
            if (position >= text.Length)
            {
                if (result.Count > 0)
                {
                    var last = result[^1];
                    result[^1] = last with { Trailing = last.Trailing + leading };
                }
                else
                {
                    result.Add(new WordSpan(leading, string.Empty, string.Empty));
                }

                break;
            }

            var coreStart = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && !IsPunctuation(text[position]))
            {
                position++;
            }

            var core = text[coreStart..position];

            var trailingStart = position;
            while (position < text.Length && IsPunctuation(text[position]))
            {
                position++;
            }

            var trailing = text[trailingStart..position];
            result.Add(new WordSpan(leading, core, trailing));
        }

        return result;
    }

    private static bool IsPunctuation(char c) => Array.IndexOf(PunctuationChars, c) >= 0;
}
