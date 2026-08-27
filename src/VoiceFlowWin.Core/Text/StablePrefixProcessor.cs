using System.Text;

namespace VoiceFlowWin.Core.Text;

/// <summary>Настройки стабилизации потоковых гипотез распознавателя.</summary>
public sealed class StablePrefixOptions
{
    /// <summary>Сколько подряд идущих гипотез должны содержать слово, чтобы считать его стабильным.</summary>
    public int RequiredRepeats { get; set; } = 2;

    /// <summary>Либо слово не менялось столько времени.</summary>
    public TimeSpan StabilityDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Сколько последних слов фразы всегда остаются изменяемым хвостом.</summary>
    public int VolatileTailWords { get; set; } = 2;

    public void Validate()
    {
        if (RequiredRepeats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RequiredRepeats), RequiredRepeats, "Нужно хотя бы одно совпадение.");
        }

        if (VolatileTailWords < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VolatileTailWords), VolatileTailWords, "Размер хвоста не может быть отрицательным.");
        }

        if (StabilityDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StabilityDelay), StabilityDelay, "Задержка стабилизации не может быть отрицательной.");
        }
    }
}

/// <summary>Результат обработки одной промежуточной гипотезы.</summary>
/// <param name="StableText">Весь подтверждённый префикс сегмента.</param>
/// <param name="VolatileTail">Изменяемый хвост: overlay показывает его всегда, поле — только в «живом» режиме.</param>
/// <param name="NewStableText">Часть, добавившаяся к стабильному префиксу именно этой гипотезой.</param>
public readonly record struct StablePrefixUpdate(string StableText, string VolatileTail, string NewStableText)
{
    /// <summary>Полный текст гипотезы: стабильная часть плюс хвост.</summary>
    public string FullText => VolatileTail.Length == 0
        ? StableText
        : StableText.Length == 0 ? VolatileTail : StableText + " " + VolatileTail;

    public bool HasNewStableText => NewStableText.Length > 0;
}

/// <summary>
/// Превращает поток переписывающих друг друга гипотез в монотонно
/// растущий текст, который можно безопасно вводить в чужое приложение.
/// </summary>
/// <remarks>
/// Потоковая модель свободно переписывает последние слова: «новый система» на следующей
/// гипотезе становится «новую систему». Если вводить каждую гипотезу целиком,
/// в поле появятся повторы; если вводить разницу вслепую — придётся стирать
/// уже введённое. Поэтому слово уходит в поле, только когда оно повторилось
/// в нескольких гипотезах подряд или достаточно долго не менялось, и при этом
/// не входит в последние <see cref="StablePrefixOptions.VolatileTailWords"/>
/// слов фразы. Подтверждённый префикс никогда не сокращается — иначе пришлось
/// бы удалять текст, который пользователь уже видит.
/// </remarks>
public sealed class StablePrefixProcessor
{
    private sealed class TokenState
    {
        public required string Text { get; set; }
        public int MatchCount { get; set; }
        public DateTimeOffset LastChangedAt { get; set; }
    }

    private readonly StablePrefixOptions _options;
    private readonly List<TokenState> _tracked = new();
    private readonly List<string> _committed = new();
    private readonly List<string> _hypotheses = new();

    public StablePrefixProcessor(StablePrefixOptions? options = null)
    {
        _options = options ?? new StablePrefixOptions();
        _options.Validate();
    }

    /// <summary>Последняя увиденная гипотеза целиком.</summary>
    public string CurrentHypothesis { get; private set; } = string.Empty;

    /// <summary>Все гипотезы сегмента — нужны для диагностики и тестов.</summary>
    public IReadOnlyList<string> Hypotheses => _hypotheses;

    /// <summary>Подтверждённый префикс.</summary>
    public string StableText => Join(_committed);

    /// <summary>Текущий изменяемый хвост.</summary>
    public string VolatileTail { get; private set; } = string.Empty;

    /// <summary>
    /// Сколько текстовых элементов приложение уже отправило в активное окно.
    /// Заполняется вызовом <see cref="NotifyInjected"/> и служит единственным
    /// источником правды при вычислении числа Backspace.
    /// </summary>
    public int InjectedElementCount { get; private set; }

    public void NotifyInjected(string injectedText) => InjectedElementCount = TextElements.Count(injectedText);

    public StablePrefixUpdate Process(string hypothesis, DateTimeOffset now)
    {
        hypothesis ??= string.Empty;
        CurrentHypothesis = hypothesis;
        _hypotheses.Add(hypothesis);

        var tokens = Tokenize(hypothesis);
        UpdateTrackedTokens(tokens, now);

        var stableCandidate = CountStableLeadingTokens(tokens, now);

        // Префикс монотонен: если движок сократил гипотезу, ранее подтверждённые
        // слова остаются — их уже видит пользователь, а исправит их Whisper.
        var target = Math.Max(_committed.Count, stableCandidate);
        var newlyCommitted = new List<string>();
        for (var i = _committed.Count; i < target && i < tokens.Count; i++)
        {
            _committed.Add(tokens[i]);
            newlyCommitted.Add(tokens[i]);
        }

        VolatileTail = tokens.Count > _committed.Count
            ? Join(tokens.Skip(_committed.Count))
            : string.Empty;

        return new StablePrefixUpdate(StableText, VolatileTail, Join(newlyCommitted));
    }

    /// <summary>
    /// Завершает сегмент: финальный потоковый результат подтверждается целиком,
    /// изменяемого хвоста больше нет.
    /// </summary>
    public StablePrefixUpdate Finalize(string? finalText, DateTimeOffset now)
    {
        // Пустой финальный результат — обычное дело: модель уже отдала все слова
        // промежуточными гипотезами. Принимать его буквально нельзя, иначе
        // изменяемый хвост, который в безопасном режиме виден только в overlay,
        // пропадёт вместе с окончанием фразы.
        var source = string.IsNullOrWhiteSpace(finalText) ? CurrentHypothesis : finalText;
        var tokens = Tokenize(source);

        // Финальный результат обычно точнее последней промежуточной
        // гипотезы, но подтверждённый префикс уже введён. Поэтому берём
        // финальный текст только в той части, что ещё не подтверждена.
        var previousCommitted = _committed.Count;
        if (tokens.Count >= previousCommitted)
        {
            var newlyCommitted = new List<string>();
            for (var i = previousCommitted; i < tokens.Count; i++)
            {
                _committed.Add(tokens[i]);
                newlyCommitted.Add(tokens[i]);
            }

            CurrentHypothesis = source;
            VolatileTail = string.Empty;
            return new StablePrefixUpdate(StableText, string.Empty, Join(newlyCommitted));
        }

        // Финальный текст короче уже подтверждённого — ничего не удаляем.
        CurrentHypothesis = source;
        VolatileTail = string.Empty;
        return new StablePrefixUpdate(StableText, string.Empty, string.Empty);
    }

    public void Reset()
    {
        _tracked.Clear();
        _committed.Clear();
        _hypotheses.Clear();
        CurrentHypothesis = string.Empty;
        VolatileTail = string.Empty;
        InjectedElementCount = 0;
    }

    private void UpdateTrackedTokens(IReadOnlyList<string> tokens, DateTimeOffset now)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i < _tracked.Count)
            {
                if (string.Equals(_tracked[i].Text, tokens[i], StringComparison.Ordinal))
                {
                    _tracked[i].MatchCount++;
                }
                else
                {
                    _tracked[i].Text = tokens[i];
                    _tracked[i].MatchCount = 1;
                    _tracked[i].LastChangedAt = now;
                }
            }
            else
            {
                _tracked.Add(new TokenState { Text = tokens[i], MatchCount = 1, LastChangedAt = now });
            }
        }

        if (_tracked.Count > tokens.Count)
        {
            _tracked.RemoveRange(tokens.Count, _tracked.Count - tokens.Count);
        }
    }

    private int CountStableLeadingTokens(IReadOnlyList<string> tokens, DateTimeOffset now)
    {
        var lastStableIndex = tokens.Count - _options.VolatileTailWords;
        var stable = 0;
        for (var i = 0; i < tokens.Count && i < lastStableIndex; i++)
        {
            var token = _tracked[i];
            var repeatedEnough = token.MatchCount >= _options.RequiredRepeats;
            var unchangedLongEnough = now - token.LastChangedAt >= _options.StabilityDelay;
            if (!repeatedEnough && !unchangedLongEnough)
            {
                break;
            }

            stable = i + 1;
        }

        return stable;
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Join(IEnumerable<string> tokens)
    {
        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(token);
        }

        return builder.ToString();
    }
}
