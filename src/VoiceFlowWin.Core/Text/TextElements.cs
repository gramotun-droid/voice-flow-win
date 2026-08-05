using System.Globalization;
using System.Text;

namespace VoiceFlowWin.Core.Text;

/// <summary>
/// Работа с текстом в единицах, которые реально удаляет Backspace.
/// </summary>
/// <remarks>
/// Длина строки .NET считает UTF-16 code units, а приложение-получатель
/// оперирует текстовыми элементами: суррогатная пара Emoji — это два char, но
/// один Backspace. Комбинирующие символы (например, «й» в разложенной форме)
/// тоже дают расхождение. Ошибка здесь означает съеденный пользовательский
/// текст, поэтому число удаляемых символов всегда считается через этот класс.
/// </remarks>
public static class TextElements
{
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var count = 0;
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>Разбивает строку на текстовые элементы.</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add((string)enumerator.Current);
        }

        return result;
    }

    /// <summary>Общий префикс двух строк, обрезанный по границе текстового элемента.</summary>
    public static int CommonPrefixLength(string left, string right)
    {
        var leftElements = Split(left);
        var rightElements = Split(right);
        var max = Math.Min(leftElements.Count, rightElements.Count);
        var common = 0;
        while (common < max && string.Equals(leftElements[common], rightElements[common], StringComparison.Ordinal))
        {
            common++;
        }

        return common;
    }

    /// <summary>Общий суффикс двух строк в текстовых элементах, не пересекающийся с уже найденным префиксом.</summary>
    public static int CommonSuffixLength(string left, string right, int reservedPrefix = 0)
    {
        var leftElements = Split(left);
        var rightElements = Split(right);
        var max = Math.Min(leftElements.Count, rightElements.Count) - reservedPrefix;
        var common = 0;
        while (common < max &&
               string.Equals(
                   leftElements[leftElements.Count - 1 - common],
                   rightElements[rightElements.Count - 1 - common],
                   StringComparison.Ordinal))
        {
            common++;
        }

        return common < 0 ? 0 : common;
    }

    /// <summary>Берёт подстроку в текстовых элементах.</summary>
    public static string Substring(string text, int startElement, int? countElements = null)
    {
        var elements = Split(text);
        if (startElement >= elements.Count)
        {
            return string.Empty;
        }

        var take = countElements ?? elements.Count - startElement;
        take = Math.Min(take, elements.Count - startElement);
        if (take <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = startElement; i < startElement + take; i++)
        {
            builder.Append(elements[i]);
        }

        return builder.ToString();
    }
}
