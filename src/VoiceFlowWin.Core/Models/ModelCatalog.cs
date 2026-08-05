namespace VoiceFlowWin.Core.Models;

public enum ModelKind
{
    Vosk,
    Whisper,
}

/// <summary>Описание модели, которую предлагает Model Manager.</summary>
public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    ModelKind Kind,
    RecognitionLanguage Language,
    string Url,
    long ApproximateSizeBytes,
    string Requirements,
    bool Recommended,
    string? Sha256 = null,
    IReadOnlyList<string>? FallbackUrls = null,
    bool AutoDownload = true)
{
    /// <summary>Источники загрузки в порядке предпочтения.</summary>
    /// <remarks>
    /// Один адрес — это одна точка отказа: скорость до конкретного хоста
    /// зависит от провайдера и страны, и наблюдались случаи отдачи около
    /// килобайта в секунду, когда загрузка формально идёт, но не заканчивается
    /// никогда. Поэтому у моделей есть запасные адреса с тем же содержимым,
    /// а совпадение файла подтверждается контрольной суммой.
    /// </remarks>
    public IEnumerable<string> DownloadUrls =>
        FallbackUrls is null ? [Url] : new[] { Url }.Concat(FallbackUrls);

    /// <summary>Vosk распространяется архивом, Whisper — одним файлом весов.</summary>
    public bool IsArchive => Kind == ModelKind.Vosk;

    public string SizeText => ApproximateSizeBytes >= 1024L * 1024 * 1024
        ? $"{ApproximateSizeBytes / 1024.0 / 1024 / 1024:0.0} ГБ"
        : $"{ApproximateSizeBytes / 1024.0 / 1024:0} МБ";
}

/// <summary>
/// Каталог рекомендуемых моделей.
/// </summary>
/// <remarks>
/// Большие модели не входят в установщик: он остался бы гигабайтным ради
/// файлов, которые части пользователей не нужны. Вместо этого при первом
/// запуске предлагается скачать рекомендованный набор — русскую и английскую
/// модели Vosk и мультиязычную модель Whisper.
///
/// Первым источником указано зеркало, а исходный сайт — запасным. Причина
/// практическая: у части провайдеров alphacephei.com и huggingface.co отдают
/// порядка килобайта в секунду, и загрузка формально идёт, но не заканчивается.
/// Подмену содержимого исключает контрольная сумма: она снята с файла,
/// совпадающего с оригиналом по размеру, и проверяется после каждой загрузки.
/// </remarks>
public static class ModelCatalog
{
    private const string VoskMirror = "https://hf-mirror.com/rhasspy/vosk-models/resolve/main";
    private const string VoskOrigin = "https://alphacephei.com/vosk/models";
    private const string WhisperMirror = "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main";
    private const string WhisperOrigin = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    public static IReadOnlyList<ModelDescriptor> All { get; } = new[]
    {
        new ModelDescriptor(
            "vosk-model-small-ru-0.22",
            "Vosk русский, компактная",
            ModelKind.Vosk,
            RecognitionLanguage.Russian,
            $"{VoskMirror}/ru/vosk-model-small-ru-0.22.zip",
            46_236_750,
            "Быстрая, подходит для потокового ввода на любом CPU.",
            Recommended: true,
            Sha256: "961D5FF98A17F4AA6DE69864D0AA71FA5BAC682301D2B5D17A3F24C5C99A46D4",
            FallbackUrls: [$"{VoskOrigin}/vosk-model-small-ru-0.22.zip"]),

        new ModelDescriptor(
            "vosk-model-ru-0.42",
            "Vosk русский, полная",
            ModelKind.Vosk,
            RecognitionLanguage.Russian,
            $"{VoskOrigin}/vosk-model-ru-0.42.zip",
            1800L * 1024 * 1024,
            "Точнее, но требует около 4 ГБ памяти. Зеркала нет: качается только с сайта Vosk, который часто отдаёт медленно, поэтому скачивание запускается вручную кнопкой «Скачать».",
            Recommended: false,
            // Единственный источник этой модели раздаёт её в разы медленнее
            // остальных. В автоматической очереди она заняла бы её на часы,
            // поэтому скачивается только по явной команде пользователя.
            AutoDownload: false),

        new ModelDescriptor(
            "vosk-model-small-en-us-0.15",
            "Vosk английский, компактная",
            ModelKind.Vosk,
            RecognitionLanguage.English,
            $"{VoskMirror}/en/vosk-model-small-en-us-0.15.zip",
            41_205_931,
            "Быстрая, подходит для потокового ввода на любом CPU.",
            Recommended: true,
            Sha256: "30F26242C4EB449F948E42CB302DD7A686CB29A3423A8367F99FF41780942498",
            FallbackUrls: [$"{VoskOrigin}/vosk-model-small-en-us-0.15.zip"]),

        new ModelDescriptor(
            "ggml-small-q5_1",
            "Whisper small, квантованная",
            ModelKind.Whisper,
            RecognitionLanguage.Auto,
            $"{WhisperMirror}/ggml-small-q5_1.bin",
            190_085_487,
            "Мультиязычная. Финализация фразы примерно за секунду на 4 ядрах.",
            Recommended: true,
            Sha256: "AE85E4A935D7A567BD102FE55AFC16BB595BDB618E11B2FC7591BC08120411BB",
            FallbackUrls: [$"{WhisperOrigin}/ggml-small-q5_1.bin"]),

        new ModelDescriptor(
            "ggml-medium-q5_0",
            "Whisper medium, квантованная",
            ModelKind.Whisper,
            RecognitionLanguage.Auto,
            $"{WhisperMirror}/ggml-medium-q5_0.bin",
            539_212_467,
            "Заметно точнее на терминах, требует 4+ ядер и 2 ГБ памяти.",
            Recommended: false,
            Sha256: "19FEA4B380C3A618EC4723C3EEF2EB785FFBA0D0538CF43F8F235E7B3B34220F",
            FallbackUrls: [$"{WhisperOrigin}/ggml-medium-q5_0.bin"]),
    };

    public static IEnumerable<ModelDescriptor> Recommended => All.Where(model => model.Recommended);

    public static ModelDescriptor? Find(string id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
}
