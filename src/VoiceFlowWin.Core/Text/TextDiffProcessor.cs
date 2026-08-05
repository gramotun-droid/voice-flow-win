namespace VoiceFlowWin.Core.Text;

/// <summary>Операция word-level diff.</summary>
public enum WordDiffKind
{
    Equal,
    Removed,
    Added,
}

public readonly record struct WordDiffOp(WordDiffKind Kind, string Word);

/// <summary>
/// План правки текста в чужом поле ввода.
/// </summary>
/// <param name="BackspaceCount">Сколько текстовых элементов удалить от каретки назад.</param>
/// <param name="TextToType">Что напечатать после удаления.</param>
public readonly record struct TextReplacementPlan(int BackspaceCount, string TextToType)
{
    public static readonly TextReplacementPlan NoOp = new(0, string.Empty);

    public bool IsNoOp => BackspaceCount == 0 && TextToType.Length == 0;
}

/// <summary>
/// Считает минимальную правку, превращающую уже введённый текст в финальный.
/// </summary>
/// <remarks>
/// Каретка стоит в конце вставленного приложением текста, поэтому удалять
/// можно только с конца: общий префикс сохраняется, всё остальное
/// перепечатывается. Общий суффикс здесь принципиально не используется —
/// «сохранить хвост» без перемещения каретки невозможно, а вслепую двигать
/// каретку в чужом приложении небезопасно.
/// </remarks>
public static class TextDiffProcessor
{
    /// <summary>
    /// Строит план замены. <paramref name="maxBackspaces"/> ограничивает
    /// удаление длиной собственного текста: приложение не имеет права стереть
    /// ни одного символа, который напечатал пользователь.
    /// </summary>
    public static TextReplacementPlan ComputeTailReplacement(string injectedText, string finalText, int maxBackspaces)
    {
        injectedText ??= string.Empty;
        finalText ??= string.Empty;

        if (string.Equals(injectedText, finalText, StringComparison.Ordinal))
        {
            return TextReplacementPlan.NoOp;
        }

        var injectedLength = TextElements.Count(injectedText);
        var commonPrefix = TextElements.CommonPrefixLength(injectedText, finalText);
        var backspaces = injectedLength - commonPrefix;

        if (backspaces > maxBackspaces)
        {
            // Правка требует зайти за границу собственного текста — отказываемся.
            return TextReplacementPlan.NoOp;
        }

        var tail = TextElements.Substring(finalText, commonPrefix);
        return new TextReplacementPlan(backspaces, tail);
    }

    /// <summary>Расстояние Левенштейна в текстовых элементах.</summary>
    public static int EditDistance(string left, string right)
    {
        var a = TextElements.Split(left);
        var b = TextElements.Split(right);
        if (a.Count == 0)
        {
            return b.Count;
        }

        if (b.Count == 0)
        {
            return a.Count;
        }

        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var j = 0; j <= b.Count; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    /// <summary>
    /// Насколько тексты похожи: 1.0 — совпадают, 0.0 — ничего общего.
    /// Используется как предохранитель: если Whisper вернул нечто совсем
    /// другое, автоматическая замена не выполняется.
    /// </summary>
    public static double Similarity(string left, string right)
    {
        var maxLength = Math.Max(TextElements.Count(left), TextElements.Count(right));
        if (maxLength == 0)
        {
            return 1.0;
        }

        return 1.0 - (double)EditDistance(left, right) / maxLength;
    }

    /// <summary>Пословный diff на основе наибольшей общей подпоследовательности.</summary>
    public static IReadOnlyList<WordDiffOp> WordDiff(string left, string right)
    {
        var a = SplitWords(left);
        var b = SplitWords(right);
        var lengths = new int[a.Count + 1, b.Count + 1];

        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var result = new List<WordDiffOp>();
        var x = 0;
        var y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (string.Equals(a[x], b[y], StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new WordDiffOp(WordDiffKind.Equal, b[y]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                result.Add(new WordDiffOp(WordDiffKind.Removed, a[x]));
                x++;
            }
            else
            {
                result.Add(new WordDiffOp(WordDiffKind.Added, b[y]));
                y++;
            }
        }

        for (; x < a.Count; x++)
        {
            result.Add(new WordDiffOp(WordDiffKind.Removed, a[x]));
        }

        for (; y < b.Count; y++)
        {
            result.Add(new WordDiffOp(WordDiffKind.Added, b[y]));
        }

        return result;
    }

    /// <summary>Длина общего пословного префикса — по нему сегменты сшиваются с предыдущим текстом.</summary>
    public static int CommonWordPrefix(string left, string right)
    {
        var a = SplitWords(left);
        var b = SplitWords(right);
        var max = Math.Min(a.Count, b.Count);
        var common = 0;
        while (common < max && string.Equals(a[common], b[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        return common;
    }

    private static IReadOnlyList<string> SplitWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
