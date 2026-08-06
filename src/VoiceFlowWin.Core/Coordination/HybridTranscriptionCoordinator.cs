using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Commands;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Core.Text;

namespace VoiceFlowWin.Core.Coordination;

public sealed class SegmentEventArgs : EventArgs
{
    public SegmentEventArgs(DictationSegment segment) => Segment = segment;

    public DictationSegment Segment { get; }
}

/// <summary>Whisper дал финальный текст, но применить его автоматически нельзя.</summary>
public sealed class ReplacementBlockedEventArgs : EventArgs
{
    public ReplacementBlockedEventArgs(DictationSegment segment, string finalText, string reason)
    {
        Segment = segment;
        FinalText = finalText;
        Reason = reason;
    }

    public DictationSegment Segment { get; }

    public string FinalText { get; }

    public string Reason { get; }
}

public sealed class InjectionProblemEventArgs : EventArgs
{
    public InjectionProblemEventArgs(InjectionFailureKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public InjectionFailureKind Kind { get; }

    public string Message { get; }
}

/// <summary>
/// Сводит вместе потоковый Zipformer и финальный Whisper и отвечает за то, какой
/// текст в итоге оказывается в чужом поле ввода.
/// </summary>
/// <remarks>
/// Главный инвариант: приложение правит только те символы, которые вставило
/// само. Для этого у каждого сегмента хранится точная строка
/// <see cref="DictationSegment.InjectedText"/>, и любая правка выражается как
/// «удалить N собственных элементов и напечатать хвост». Поиск фразы по
/// документу не используется никогда — в поле может оказаться сколько угодно
/// одинаковых слов, и промах означал бы порчу чужого текста.
/// </remarks>
public sealed class HybridTranscriptionCoordinator
{
    private readonly ITextInjectionService _injection;
    private readonly IFocusTracker _focusTracker;
    private readonly IInputInterventionMonitor _interventionMonitor;
    private readonly DictionaryProcessor _dictionary;
    private readonly VoiceCommandProcessor _commands;
    private readonly ISettingsService _settings;
    private readonly ILogger<HybridTranscriptionCoordinator> _logger;

    private readonly Dictionary<long, SegmentContext> _segments = new();
    private readonly SemaphoreSlim _injectionLock = new(1, 1);
    private long _lastSegmentId;

    /// <summary>Текст, введённый приложением за текущий сеанс диктовки — контекст для регистра и пробелов.</summary>
    private string _sessionText = string.Empty;

    /// <summary>Звук всего сеанса — исходник для финального прохода после остановки.</summary>
    private readonly List<byte[]> _sessionAudio = new();

    private long _sessionAudioBytes;

    public HybridTranscriptionCoordinator(
        ITextInjectionService injection,
        IFocusTracker focusTracker,
        IInputInterventionMonitor interventionMonitor,
        DictionaryProcessor dictionary,
        VoiceCommandProcessor commands,
        ISettingsService settings,
        ILogger<HybridTranscriptionCoordinator>? logger = null)
    {
        _injection = injection;
        _focusTracker = focusTracker;
        _interventionMonitor = interventionMonitor;
        _dictionary = dictionary;
        _commands = commands;
        _settings = settings;
        _logger = logger ?? NullLogger<HybridTranscriptionCoordinator>.Instance;
    }

    public event EventHandler<SegmentEventArgs>? SegmentUpdated;

    public event EventHandler<ReplacementBlockedEventArgs>? ReplacementBlocked;

    public event EventHandler<InjectionProblemEventArgs>? InjectionProblem;

    public DictationSegment? CurrentSegment { get; private set; }

    /// <summary>Последние подтверждённые слова — контекст для Whisper.</summary>
    public string SessionText => _sessionText;

    private sealed class SegmentContext
    {
        public required DictationSegment Segment { get; init; }

        public required StablePrefixProcessor Prefix { get; init; }

        /// <summary>Разделитель перед сегментом; он тоже принадлежит приложению и участвует в замене.</summary>
        public required string Separator { get; set; }

        /// <summary>Текст сеанса до начала сегмента — база для регистра первой буквы.</summary>
        public required string ContextBefore { get; init; }
    }

    public DictationSegment BeginSegment(RecognitionLanguage language, WindowFocusSnapshot? focus = null)
    {
        var snapshot = focus ?? _focusTracker.Capture();
        var segment = new DictationSegment(++_lastSegmentId, language, snapshot, DateTimeOffset.UtcNow);

        var streamingSettings = _settings.Current.Streaming;
        var prefix = new StablePrefixProcessor(new StablePrefixOptions
        {
            RequiredRepeats = streamingSettings.StableRepeats,
            StabilityDelay = TimeSpan.FromMilliseconds(streamingSettings.StabilityDelayMs),
            VolatileTailWords = streamingSettings.VolatileTailWords,
        });

        _segments[segment.SegmentId] = new SegmentContext
        {
            Segment = segment,
            Prefix = prefix,
            Separator = string.Empty,
            ContextBefore = _sessionText,
        };

        CurrentSegment = segment;
        _interventionMonitor.StartWatching(snapshot);
        SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
        return segment;
    }

    /// <summary>Обрабатывает очередную промежуточную гипотезу потоковой модели.</summary>
    public async Task OnPartialResultAsync(long segmentId, string hypothesis, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(segmentId, out var context) || context.Segment.State.IsTerminal())
        {
            return;
        }

        var update = context.Prefix.Process(hypothesis, now);
        context.Segment.RecordHypothesis(update.FullText, update.StableText, update.VolatileTail);

        var liveTextMode = _settings.Current.General.LiveTextMode;

        // Во время речи поле не трогается вовсе: фраза попадёт в него один раз
        // и уже проверенной. Пользователь видит распознанное в overlay.
        if (liveTextMode != LiveTextMode.InsertAfterPause)
        {
            var visibleText = liveTextMode == LiveTextMode.MaximumLive
                ? update.FullText
                : update.StableText;

            await SyncInjectionAsync(context, PrepareInterimText(context, visibleText), cancellationToken).ConfigureAwait(false);
        }

        SegmentUpdated?.Invoke(this, new SegmentEventArgs(context.Segment));
    }

    /// <summary>
    /// Закрывает сегмент: финальный текст потоковой модели дописывается целиком, включая
    /// изменяемый хвост, который в безопасном режиме был только в overlay.
    /// </summary>
    public async Task EndSegmentAsync(long segmentId, string? voskFinalText, byte[] audio, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(segmentId, out var context) || context.Segment.State.IsTerminal())
        {
            return;
        }

        var update = context.Prefix.Finalize(voskFinalText, DateTimeOffset.UtcNow);
        context.Segment.RecordHypothesis(update.StableText, update.StableText, string.Empty);
        context.Segment.SetAudio(audio);
        RememberSessionAudio(audio);

        // В режиме вставки после паузы текст потоковой модели в поле не попадает: его
        // задача — показать речь в overlay, а в поле уйдёт результат Whisper.
        // Если Whisper не справится, текст потоковой модели вставит обработчик отказа.
        if (_settings.Current.General.LiveTextMode != LiveTextMode.InsertAfterPause)
        {
            await SyncInjectionAsync(context, PrepareInterimText(context, update.StableText), cancellationToken).ConfigureAwait(false);
        }

        context.Segment.MarkAwaitingFinalization(DateTimeOffset.UtcNow);
        _sessionText = context.ContextBefore + context.Segment.InjectedText;
        SegmentUpdated?.Invoke(this, new SegmentEventArgs(context.Segment));
    }

    /// <summary>Применяет результат Whisper к своему и только к своему сегменту.</summary>
    public async Task ApplyFinalRecognitionAsync(FinalRecognitionResult result, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(result.SegmentId, out var context))
        {
            _logger.LogDebug("Результат Whisper для неизвестного сегмента {SegmentId} отброшен.", result.SegmentId);
            return;
        }

        var segment = context.Segment;

        if (!result.Succeeded)
        {
            // В режиме вставки после паузы в поле ещё ничего нет, и отказ
            // Whisper означал бы потерю фразы целиком. Вставляем то, что
            // услышала потоковая модель: хуже по качеству, но лучше, чем ничего.
            if (IsDeferredInsert && segment.InjectedLength == 0 && segment.StableText.Length > 0)
            {
                var fallback = PrepareInterimText(context, segment.StableText);
                if (await SyncInjectionAsync(context, fallback, cancellationToken).ConfigureAwait(false))
                {
                    segment.MarkFinalized(context.Separator + fallback);
                    _sessionText = context.ContextBefore + segment.InjectedText;
                    SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
                    return;
                }
            }

            segment.MarkFailed(result.Error ?? "Whisper не смог обработать сегмент.");
            SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
            return;
        }

        var finalText = BuildFinalText(context, result.Text);
        if (finalText.Length == 0)
        {
            segment.MarkFinalized(segment.InjectedText);
            SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
            return;
        }

        var injectionSettings = _settings.Current.Injection;

        // Запрет автоматической замены защищает уже введённый текст. Если в поле
        // ничего не вводилось, заменять нечего — это обычная вставка.
        if (!injectionSettings.SafeFinalReplacement && segment.InjectedLength > 0)
        {
            segment.MarkFrozenWithResult(finalText);
            ReplacementBlocked?.Invoke(this, new ReplacementBlockedEventArgs(segment, finalText, "Автоматическая замена отключена в настройках."));
            return;
        }

        var blockReason = GetReplacementBlockReason(context, finalText);
        if (blockReason is not null)
        {
            segment.MarkFrozenWithResult(finalText);
            ReplacementBlocked?.Invoke(this, new ReplacementBlockedEventArgs(segment, finalText, blockReason));
            SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
            return;
        }

        var applied = await SyncInjectionAsync(context, finalText, cancellationToken).ConfigureAwait(false);
        if (applied)
        {
            segment.MarkFinalized(context.Separator + finalText);
            _sessionText = context.ContextBefore + segment.InjectedText;
        }
        else
        {
            segment.MarkFrozenWithResult(finalText);
            ReplacementBlocked?.Invoke(this, new ReplacementBlockedEventArgs(segment, finalText, "Не удалось безопасно заменить текст."));
        }

        if (!_settings.Current.Privacy.StoreAudio)
        {
            segment.ReleaseAudio();
        }

        SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
    }

    /// <summary>Ручная замена по кнопке overlay: пользователь сам разрешил правку.</summary>
    public async Task<bool> ApplyManualReplacementAsync(long segmentId, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(segmentId, out var context) || context.Segment.WhisperText is null)
        {
            return false;
        }

        var target = context.Segment.WhisperText;
        var applied = await SyncInjectionAsync(context, target, cancellationToken, force: true).ConfigureAwait(false);
        if (applied)
        {
            context.Segment.MarkFinalized(context.Separator + target);
            _sessionText = context.ContextBefore + context.Segment.InjectedText;
            SegmentUpdated?.Invoke(this, new SegmentEventArgs(context.Segment));
        }

        return applied;
    }

    /// <summary>Отменяет сегмент и стирает собственный текст, если это безопасно.</summary>
    public async Task CancelSegmentAsync(long segmentId, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(segmentId, out var context))
        {
            return;
        }

        var segment = context.Segment;
        if (!segment.UserIntervened && segment.InjectedLength > 0 && IsTargetUnchanged(segment))
        {
            using (_interventionMonitor.SuppressSelfInput())
            {
                await _injection.DeleteBackwardAsync(segment.InjectedLength, cancellationToken).ConfigureAwait(false);
            }

            segment.RecordInjection(string.Empty);
        }

        segment.MarkCancelled();
        segment.ReleaseAudio();
        _sessionText = context.ContextBefore;
        SegmentUpdated?.Invoke(this, new SegmentEventArgs(segment));
    }

    /// <summary>Регистрирует вмешательство пользователя: с этого момента автозамена запрещена.</summary>
    public void NotifyIntervention(InterventionKind kind, string description)
    {
        if (kind == InterventionKind.None || CurrentSegment is null)
        {
            return;
        }

        CurrentSegment.MarkUserIntervention(description);
        _logger.LogInformation("Сегмент {SegmentId} заморожен: {Reason}", CurrentSegment.SegmentId, description);
        SegmentUpdated?.Invoke(this, new SegmentEventArgs(CurrentSegment));
    }

    /// <summary>Заканчивает сеанс диктовки: контекст сбрасывается, история сегментов очищается.</summary>
    public void EndSession()
    {
        _interventionMonitor.StopWatching();
        CurrentSegment = null;
        _sessionText = string.Empty;
        _sessionAudio.Clear();
        _sessionAudioBytes = 0;

        foreach (var context in _segments.Values)
        {
            context.Segment.ReleaseAudio();
        }

        // Сегменты храним до конца сеанса: запоздавший результат Whisper
        // должен найти свой сегмент, а не примениться к чужому.
        _segments.Clear();
    }

    /// <summary>Звук всей диктовки одним куском — вход финального прохода.</summary>
    /// <remarks>
    /// Пустой массив означает, что проходить нечего: либо не было речи, либо
    /// сеанс оказался длиннее допустимого и звук не копился.
    /// </remarks>
    public byte[] BuildSessionAudio()
    {
        if (_sessionAudio.Count == 0)
        {
            return [];
        }

        var combined = new byte[_sessionAudioBytes];
        var offset = 0;
        foreach (var chunk in _sessionAudio)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }

    /// <summary>Весь текст, введённый приложением за сеанс, в порядке фраз.</summary>
    public string SessionInjectedText => string.Concat(
        _segments.Values
            .OrderBy(context => context.Segment.SegmentId)
            .Select(context => context.Segment.InjectedText));

    /// <summary>
    /// Заменяет весь введённый за сеанс текст результатом финального прохода.
    /// </summary>
    /// <remarks>
    /// Проход по всей диктовке видит фразы вместе, поэтому согласует формы слов
    /// и пунктуацию между ними — того, что пофразная обработка сделать не может.
    /// Замена выполняется тем же способом, что и пофразная: удаляется ровно
    /// столько собственных элементов, сколько было введено, и печатается новый
    /// текст. Если пользователь успел вмешаться или сменить окно, замена не
    /// выполняется — правка чужого текста недопустима.
    /// </remarks>
    public async Task<bool> ApplySessionCorrectionAsync(string correctedText, CancellationToken cancellationToken)
    {
        var injected = SessionInjectedText;
        if (injected.Length == 0 || string.IsNullOrWhiteSpace(correctedText))
        {
            return false;
        }

        var normalized = TextNormalizer.NormalizeWhitespace(correctedText);
        if (string.Equals(normalized, injected.TrimStart(), StringComparison.Ordinal))
        {
            return false;
        }

        var last = _segments.Values
            .OrderBy(context => context.Segment.SegmentId)
            .LastOrDefault();

        if (last is null || last.Segment.UserIntervened || !IsTargetUnchanged(last.Segment))
        {
            _logger.LogInformation("Финальный проход не применён: текст правился или сменилось окно.");
            return false;
        }

        // Разделитель первой фразы принадлежит приложению и заменяется вместе
        // с текстом, поэтому он восстанавливается перед новым текстом.
        var separator = injected.Length > 0 && char.IsWhiteSpace(injected[0])
            ? injected[..1]
            : string.Empty;

        var desired = separator + normalized;

        await _injectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elements = TextElements.Count(injected);

            using (_interventionMonitor.SuppressSelfInput())
            {
                if (!await _injection.DeleteBackwardAsync(elements, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("Финальный проход не применён: не удалось удалить собственный текст.");
                    return false;
                }

                var result = await _injection.InjectAsync(desired, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    InjectionProblem?.Invoke(this, new InjectionProblemEventArgs(result.Failure, result.Message ?? "Не удалось вставить текст."));
                    return false;
                }

                // Весь введённый текст теперь принадлежит последней фразе:
                // именно её строка описывает то, что стоит в поле.
                foreach (var context in _segments.Values)
                {
                    context.Segment.RecordInjection(string.Empty);
                }

                last.Segment.RecordInjection(result.InjectedText);
                _sessionText = result.InjectedText;
            }

            return true;
        }
        finally
        {
            _injectionLock.Release();
        }
    }

    private void RememberSessionAudio(byte[] audio)
    {
        if (audio.Length == 0)
        {
            return;
        }

        // Час диктовки — это больше сотни мегабайт звука. Предел заведомо выше
        // обычного сеанса, но не даёт памяти расти бесконечно.
        const long limitBytes = 200L * 1024 * 1024;

        if (_sessionAudioBytes + audio.Length > limitBytes)
        {
            return;
        }

        _sessionAudio.Add(audio);
        _sessionAudioBytes += audio.Length;
    }

    public DictationSegment? FindSegment(long segmentId) =>
        _segments.TryGetValue(segmentId, out var context) ? context.Segment : null;

    /// <summary>Короткий контекст предыдущего текста для Whisper.</summary>
    public string? BuildWhisperContext()
    {
        var whisperSettings = _settings.Current.Whisper;
        if (!whisperSettings.UsePreviousTextContext || _sessionText.Length == 0)
        {
            return null;
        }

        var words = _sessionText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= whisperSettings.MaxContextWords
            ? _sessionText
            : string.Join(' ', words[^whisperSettings.MaxContextWords..]);
    }

    /// <summary>Фраза вставляется целиком после паузы, а не по мере речи.</summary>
    private bool IsDeferredInsert =>
        _settings.Current.General.LiveTextMode == LiveTextMode.InsertAfterPause;

    private string PrepareInterimText(SegmentContext context, string rawText)
    {
        if (rawText.Length == 0)
        {
            return string.Empty;
        }

        var text = _dictionary.Apply(rawText, context.Segment.Language);
        var commandResult = _commands.Process(text, context.Segment.Language);
        text = TextNormalizer.NormalizeWhitespace(commandResult.Text);

        if (TextNormalizer.StartsNewSentence(context.ContextBefore))
        {
            text = TextNormalizer.CapitalizeFirstLetter(text);
        }

        return text;
    }

    private string BuildFinalText(SegmentContext context, string whisperText)
    {
        var text = _dictionary.Apply(whisperText, context.Segment.Language);
        var commandResult = _commands.Process(text, context.Segment.Language);
        return TextNormalizer.PrepareSegmentText(
            commandResult.Text,
            context.ContextBefore,
            _settings.Current.General.AutomaticPunctuation);
    }

    private string? GetReplacementBlockReason(SegmentContext context, string finalText)
    {
        var segment = context.Segment;
        var injectionSettings = _settings.Current.Injection;

        if (segment.UserIntervened && injectionSettings.BlockReplacementAfterIntervention)
        {
            return "Пользователь правил текст после начала фразы.";
        }

        if (segment.State is SegmentState.Cancelled or SegmentState.Failed)
        {
            return "Сегмент уже завершён без результата.";
        }

        var intervention = _interventionMonitor.CheckNow(segment.StartFocus);
        if (intervention != InterventionKind.None)
        {
            segment.MarkUserIntervention(intervention.ToString());
            return "Изменилось окно или позиция курсора.";
        }

        var injectedCore = TrimSeparator(segment.InjectedText, context.Separator);
        if (injectedCore.Length > 0 &&
            TextDiffProcessor.Similarity(injectedCore, finalText) < injectionSettings.MinimumReplacementSimilarity)
        {
            // Whisper вернул текст, почти не похожий на услышанное потоковой моделью.
            // Скорее всего это ошибка распознавания, а не исправление.
            return "Результат Whisper слишком сильно отличается от введённого текста.";
        }

        return null;
    }

    /// <summary>
    /// Приводит текст в поле к <paramref name="desiredCore"/>: удаляет ровно
    /// столько собственных элементов, сколько нужно, и допечатывает остаток.
    /// </summary>
    private async Task<bool> SyncInjectionAsync(
        SegmentContext context,
        string desiredCore,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var segment = context.Segment;

        if (desiredCore.Length > 0 && segment.InjectedText.Length == 0)
        {
            context.Separator = TextNormalizer.NeedsSeparatingSpace(context.ContextBefore, desiredCore) ? " " : string.Empty;
        }

        var desired = desiredCore.Length == 0 ? string.Empty : context.Separator + desiredCore;
        if (string.Equals(desired, segment.InjectedText, StringComparison.Ordinal))
        {
            return true;
        }

        if (!force && segment.UserIntervened)
        {
            return false;
        }

        await _injectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && !IsTargetUnchanged(segment))
            {
                segment.MarkUserIntervention("Сменилось окно или элемент ввода.");
                return false;
            }

            var plan = TextDiffProcessor.ComputeTailReplacement(segment.InjectedText, desired, segment.InjectedLength);
            if (plan.IsNoOp)
            {
                // Правка потребовала бы удалить чужой текст — отказываемся.
                return string.Equals(desired, segment.InjectedText, StringComparison.Ordinal);
            }

            using (_interventionMonitor.SuppressSelfInput())
            {
                if (plan.BackspaceCount > 0)
                {
                    var deleted = await _injection.DeleteBackwardAsync(plan.BackspaceCount, cancellationToken).ConfigureAwait(false);
                    if (!deleted)
                    {
                        _logger.LogWarning("Не удалось удалить {Count} собственных символов.", plan.BackspaceCount);
                        return false;
                    }

                    var keptLength = segment.InjectedLength - plan.BackspaceCount;
                    segment.RecordInjection(TextElements.Substring(segment.InjectedText, 0, keptLength));
                }

                if (plan.TextToType.Length > 0)
                {
                    var result = await _injection.InjectAsync(plan.TextToType, cancellationToken).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        InjectionProblem?.Invoke(this, new InjectionProblemEventArgs(result.Failure, result.Message ?? "Не удалось вставить текст."));
                        segment.MarkFailed(result.Message ?? "Не удалось вставить текст.");
                        return false;
                    }

                    segment.RecordInjection(segment.InjectedText + result.InjectedText);
                }
            }

            context.Prefix.NotifyInjected(segment.InjectedText);
            return true;
        }
        finally
        {
            _injectionLock.Release();
        }
    }

    private bool IsTargetUnchanged(DictationSegment segment) =>
        _interventionMonitor.CheckNow(segment.StartFocus) == InterventionKind.None;

    private static string TrimSeparator(string injectedText, string separator) =>
        separator.Length > 0 && injectedText.StartsWith(separator, StringComparison.Ordinal)
            ? injectedText[separator.Length..]
            : injectedText;
}
