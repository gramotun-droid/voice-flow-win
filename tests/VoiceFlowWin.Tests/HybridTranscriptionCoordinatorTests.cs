using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Commands;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Tests.Fakes;
using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>Обязательные сценарии из раздела 35 технического задания.</summary>
public class HybridTranscriptionCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public Harness(LiveTextMode mode = LiveTextMode.MaximumLive)
        {
            Field = new FakeTextField();
            Injection = new FakeInjectionService(Field);
            Tracker = new FakeFocusTracker();
            Monitor = new FakeInterventionMonitor(Tracker);
            Settings = new FakeSettingsService();
            Settings.Mutate(current => current.General.LiveTextMode = mode);
            Coordinator = new HybridTranscriptionCoordinator(
                Injection,
                Tracker,
                Monitor,
                new DictionaryProcessor(DefaultDictionary.Create()),
                new VoiceCommandProcessor(),
                Settings);
        }

        public FakeTextField Field { get; }

        public FakeInjectionService Injection { get; }

        public FakeFocusTracker Tracker { get; }

        public FakeInterventionMonitor Monitor { get; }

        public FakeSettingsService Settings { get; }

        public HybridTranscriptionCoordinator Coordinator { get; }

        public async Task<DictationSegment> DictateAsync(IEnumerable<string> hypotheses, string voskFinal, RecognitionLanguage language = RecognitionLanguage.Russian)
        {
            var segment = Coordinator.BeginSegment(language);
            var time = Start;
            foreach (var hypothesis in hypotheses)
            {
                await Coordinator.OnPartialResultAsync(segment.SegmentId, hypothesis, time, CancellationToken.None);
                time = time.AddMilliseconds(200);
            }

            await Coordinator.EndSegmentAsync(segment.SegmentId, voskFinal, Array.Empty<byte>(), CancellationToken.None);
            return segment;
        }

        public Task ApplyWhisperAsync(long segmentId, string text) =>
            Coordinator.ApplyFinalRecognitionAsync(
                new FinalRecognitionResult(segmentId, text, TimeSpan.FromMilliseconds(400), true),
                CancellationToken.None);
    }

    [Fact]
    public async Task Сценарий_1_Потоковый_ввод_не_создаёт_повторов()
    {
        var harness = new Harness();

        await harness.DictateAsync(
            new[]
            {
                "разработать",
                "разработать новую",
                "разработать новую систему",
                "разработать новую систему распознавания",
            },
            "разработать новую систему распознавания");

        Assert.Equal("Разработать новую систему распознавания", harness.Field.Content);
    }

    [Fact]
    public async Task Старый_режим_после_паузы_не_скрывает_текущий_текст()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);

        var segment = await harness.DictateAsync(
            new[] { "нам нужно", "нам нужно разработать", "нам нужно разработать систему" },
            "нам нужно разработать систему");

        // Сохранённая настройка старой версии больше не должна возвращать
        // серый невведённый хвост.
        Assert.Equal("Нам нужно разработать систему", harness.Field.Content);
        Assert.Equal("нам нужно разработать систему", segment.StableText);
        Assert.Equal(SegmentState.Finalized, segment.State);
    }

    [Fact]
    public async Task Завершение_сегмента_сохраняет_потоковый_текст()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);

        var segment = await harness.DictateAsync(new[] { "фраза целиком" }, "фраза целиком");

        // Никакого второго распознавания после этого не запускается.
        Assert.Equal("Фраза целиком", harness.Field.Content);
        Assert.Equal(SegmentState.Finalized, segment.State);
    }

    [Fact]
    public async Task Вставка_после_паузы_не_дописывает_фразу_дважды()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);

        var first = await harness.DictateAsync(new[] { "первая фраза" }, "первая фраза");
        var second = await harness.DictateAsync(new[] { "вторая фраза" }, "вторая фраза");

        Assert.Equal("Первая фраза вторая фраза", harness.Field.Content);
    }

    [Fact]
    public async Task Проход_по_всей_диктовке_заменяет_весь_введённый_текст()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);

        var first = await harness.DictateAsync(new[] { "первая фраза" }, "первая фраза");
        await harness.ApplyWhisperAsync(first.SegmentId, "первая фраза");

        var second = await harness.DictateAsync(new[] { "вторая фраза" }, "вторая фраза");
        await harness.ApplyWhisperAsync(second.SegmentId, "вторая фраза");

        var applied = await harness.Coordinator.ApplySessionCorrectionAsync(
            "Первая фраза, вторая фраза.",
            CancellationToken.None);

        Assert.True(applied);
        Assert.Equal("Первая фраза, вторая фраза.", harness.Field.Content);
    }

    [Fact]
    public async Task Проход_по_всей_диктовке_не_трогает_текст_после_вмешательства()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);

        var segment = await harness.DictateAsync(new[] { "первая фраза" }, "первая фраза");
        await harness.ApplyWhisperAsync(segment.SegmentId, "Первая фраза.");
        var beforePass = harness.Field.Content;

        // Пользователь щёлкнул в другом окне — трогать текст больше нельзя.
        harness.Tracker.SwitchWindow();

        var applied = await harness.Coordinator.ApplySessionCorrectionAsync(
            "Совсем другой текст.",
            CancellationToken.None);

        Assert.False(applied);
        Assert.Equal(beforePass, harness.Field.Content);
    }

    [Fact]
    public async Task Звук_всей_диктовки_собирается_для_прохода()
    {
        var harness = new Harness(LiveTextMode.InsertAfterPause);
        var audio = new byte[] { 1, 2, 3, 4 };

        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "фраза", Start, CancellationToken.None);
        await harness.Coordinator.EndSegmentAsync(segment.SegmentId, "фраза", audio, CancellationToken.None);

        var second = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);
        await harness.Coordinator.EndSegmentAsync(second.SegmentId, "вторая", audio, CancellationToken.None);

        Assert.Equal(audio.Length * 2, harness.Coordinator.BuildSessionAudio().Length);

        harness.Coordinator.EndSession();
        Assert.Empty(harness.Coordinator.BuildSessionAudio());
    }

    [Fact]
    public async Task Потоковый_финал_не_запускает_последующую_замену()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(
            new[] { "нам нужно разработать новый", "нам нужно разработать новый система" },
            "нам нужно разработать новый система");

        Assert.Equal("Нам нужно разработать новый система", harness.Field.Content);
        Assert.Equal(SegmentState.Finalized, segment.State);
    }

    [Fact]
    public async Task Завершение_не_переписывает_уже_напечатанный_текст()
    {
        var harness = new Harness();
        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);

        await harness.Coordinator.OnPartialResultAsync(
            segment.SegmentId,
            "текущий набранный текст",
            Start,
            CancellationToken.None);
        var beforeStop = harness.Field.Content;

        await harness.Coordinator.EndSegmentAsync(
            segment.SegmentId,
            "совсем другой результат при завершении",
            Array.Empty<byte>(),
            CancellationToken.None);

        Assert.Equal(beforeStop, harness.Field.Content);
        Assert.Equal(SegmentState.Finalized, segment.State);
    }

    [Fact]
    public async Task Ошибка_печати_после_удаления_восстанавливает_прежний_хвост()
    {
        var harness = new Harness();
        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);

        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "нам нужно новый", Start, CancellationToken.None);
        var beforeCorrection = harness.Field.Content;
        harness.Injection.FailNextInjection = true;

        await harness.Coordinator.OnPartialResultAsync(
            segment.SegmentId,
            "нам нужно новую",
            Start.AddMilliseconds(200),
            CancellationToken.None);

        Assert.Equal(beforeCorrection, harness.Field.Content);
        Assert.Equal(SegmentState.Failed, segment.State);
    }

    [Fact]
    public async Task Предыдущий_сегмент_не_переписывается()
    {
        var harness = new Harness();

        var first = await harness.DictateAsync(new[] { "первая фраза целиком" }, "первая фраза целиком");
        var afterFirst = harness.Field.Content;

        var second = await harness.DictateAsync(new[] { "вторая фраза" }, "вторая фраза");

        Assert.StartsWith(afterFirst, harness.Field.Content, StringComparison.Ordinal);
        Assert.Equal("Первая фраза целиком вторая фраза", harness.Field.Content);
    }

    [Fact]
    public async Task Сценарий_3_После_ввода_пользователя_замена_не_выполняется()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(new[] { "нам нужно разработать" }, "нам нужно разработать");
        var textBefore = harness.Field.Content;

        // Пользователь сам дописал слово.
        harness.Field.UserTypes(" вручную");
        harness.Coordinator.NotifyIntervention(InterventionKind.UserTyped, "Пользователь печатал.");

        string? blockedReason = null;
        harness.Coordinator.ReplacementBlocked += (_, args) => blockedReason = args.Reason;

        await harness.ApplyWhisperAsync(segment.SegmentId, "Нам нужно разработать систему.");

        Assert.Equal(textBefore + " вручную", harness.Field.Content);
        Assert.Equal(SegmentState.Frozen, segment.State);
        Assert.NotNull(blockedReason);
        Assert.Equal("Нам нужно разработать систему.", segment.WhisperText);
    }

    [Fact]
    public async Task Сценарий_4_После_смены_окна_результат_только_в_overlay()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(new[] { "текст в блокноте" }, "текст в блокноте");
        var textBefore = harness.Field.Content;

        harness.Tracker.SwitchWindow();

        ReplacementBlockedEventArgs? blocked = null;
        harness.Coordinator.ReplacementBlocked += (_, args) => blocked = args;

        await harness.ApplyWhisperAsync(segment.SegmentId, "Текст в блокноте.");

        Assert.Equal(textBefore, harness.Field.Content);
        Assert.Equal(SegmentState.Frozen, segment.State);
        Assert.NotNull(blocked);
        Assert.Equal("Текст в блокноте.", blocked!.FinalText);
    }

    [Fact]
    public async Task Сценарий_5_После_перемещения_курсора_сегмент_замораживается()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(new[] { "проверка курсора" }, "проверка курсора");
        var textBefore = harness.Field.Content;

        harness.Monitor.Forced = InterventionKind.CaretMoved;

        await harness.ApplyWhisperAsync(segment.SegmentId, "Проверка курсора.");

        Assert.Equal(textBefore, harness.Field.Content);
        Assert.Equal(SegmentState.Frozen, segment.State);
    }

    [Fact]
    public async Task Сценарий_6_Отмена_сегмента_стирает_только_свой_текст()
    {
        var harness = new Harness();

        harness.Field.UserTypes("Пользовательский текст.");
        var userText = harness.Field.Content;

        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);

        // Двух одинаковых гипотез достаточно, чтобы префикс подтвердился и ушёл в поле.
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "лишняя фраза здесь совсем", Start, CancellationToken.None);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "лишняя фраза здесь совсем", Start.AddMilliseconds(200), CancellationToken.None);
        Assert.NotEqual(userText, harness.Field.Content);

        await harness.Coordinator.CancelSegmentAsync(segment.SegmentId, CancellationToken.None);

        Assert.Equal(userText, harness.Field.Content);
        Assert.Equal(SegmentState.Cancelled, segment.State);
    }

    [Fact]
    public async Task Сценарий_7_Английские_термины_внутри_русской_речи()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(
            new[] { "открой гитхаб и создай пул", "открой гитхаб и создай пул реквест" },
            "открой гитхаб и создай пул реквест");

        // Промежуточный результат уже показывает термины латиницей.
        Assert.Contains("GitHub", harness.Field.Content, StringComparison.Ordinal);

        Assert.Equal("Открой GitHub и создай pull request", harness.Field.Content);
    }

    [Fact]
    public async Task Слишком_непохожий_результат_Whisper_не_применяется()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(new[] { "привет как дела" }, "привет как дела");
        var textBefore = harness.Field.Content;

        await harness.ApplyWhisperAsync(segment.SegmentId, "Совершенно посторонний фрагмент речи.");

        Assert.Equal(textBefore, harness.Field.Content);
        Assert.Equal(SegmentState.Frozen, segment.State);
    }

    [Fact]
    public async Task Ручная_замена_работает_даже_после_заморозки()
    {
        var harness = new Harness();

        var segment = await harness.DictateAsync(new[] { "проверка ручной замены" }, "проверка ручной замены");
        harness.Monitor.Forced = InterventionKind.CaretMoved;
        await harness.ApplyWhisperAsync(segment.SegmentId, "Проверка ручной замены.");
        Assert.Equal(SegmentState.Frozen, segment.State);

        var applied = await harness.Coordinator.ApplyManualReplacementAsync(segment.SegmentId, CancellationToken.None);

        Assert.True(applied);
        Assert.Equal("Проверка ручной замены.", harness.Field.Content);
    }

    [Fact]
    public async Task Максимально_живой_режим_вводит_и_хвост()
    {
        var harness = new Harness();
        harness.Settings.Mutate(settings => settings.General.LiveTextMode = LiveTextMode.MaximumLive);

        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "нам нужно новый", Start, CancellationToken.None);

        Assert.Equal("Нам нужно новый", harness.Field.Content);

        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "нам нужно новую", Start.AddMilliseconds(200), CancellationToken.None);

        // Хвост переписан, но стёрт только собственный текст приложения.
        Assert.Equal("Нам нужно новую", harness.Field.Content);
        Assert.True(harness.Field.TotalDeletedElements > 0);
    }

    [Fact]
    public async Task Старый_безопасный_режим_тоже_печатает_и_обновляет_весь_хвост()
    {
        var harness = new Harness();

        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "нам нужно новый система", Start, CancellationToken.None);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "нам нужно новую систему", Start.AddMilliseconds(200), CancellationToken.None);

        Assert.Equal("Нам нужно новую систему", harness.Field.Content);
        Assert.True(harness.Field.TotalDeletedElements > 0);
    }

    [Fact]
    public async Task Отказ_вставки_из_за_прав_сообщается_наверх()
    {
        var harness = new Harness();
        harness.Injection.FailNextInjection = true;
        harness.Injection.FailureKind = InjectionFailureKind.PrivilegeBlocked;

        InjectionProblemEventArgs? problem = null;
        harness.Coordinator.InjectionProblem += (_, args) => problem = args;

        var segment = harness.Coordinator.BeginSegment(RecognitionLanguage.Russian);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "текст в окне администратора", Start, CancellationToken.None);
        await harness.Coordinator.OnPartialResultAsync(segment.SegmentId, "текст в окне администратора", Start.AddMilliseconds(200), CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Equal(InjectionFailureKind.PrivilegeBlocked, problem!.Kind);
        Assert.Equal(SegmentState.Failed, segment.State);
        Assert.Equal(string.Empty, harness.Field.Content);
    }

    [Fact]
    public void Контекст_для_Whisper_ограничен_по_числу_слов()
    {
        var harness = new Harness();
        harness.Settings.Mutate(settings => settings.Whisper.MaxContextWords = 3);

        Assert.Null(harness.Coordinator.BuildWhisperContext());
    }
}
