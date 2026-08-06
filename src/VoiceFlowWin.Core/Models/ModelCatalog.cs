namespace VoiceFlowWin.Core.Models;

public enum ModelKind
{
    /// <summary>Потоковая модель распознавания (sherpa-onnx, Zipformer).</summary>
    Streaming,

    /// <summary>Модель финальной проверки фразы (whisper.cpp).</summary>
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

    /// <summary>Потоковая модель распространяется архивом, Whisper — одним файлом весов.</summary>
    public bool IsArchive => Kind == ModelKind.Streaming;

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
    /// <summary>Модели sherpa-onnx лежат в релизе проекта на GitHub.</summary>
    /// <remarks>
    /// GitHub отдаёт их быстро и без зеркал, в отличие от huggingface.co: у
    /// части провайдеров тот выдаёт около килобайта в секунду.
    /// </remarks>
    private const string SherpaModels = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

    private const string WhisperMirror = "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main";
    private const string WhisperOrigin = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    public static IReadOnlyList<ModelDescriptor> All { get; } = new[]
    {
        new ModelDescriptor(
            "sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16",
            "Zipformer русский, компактная",
            ModelKind.Streaming,
            RecognitionLanguage.Russian,
            $"{SherpaModels}/sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16.tar.bz2",
            24_110_855,
            "Квантованная, работает на любом процессоре и весит 24 МБ.",
            Recommended: true,
            Sha256: "6BA68A01FF3C5445AAF2D61E9B97B026F1149DCC9049D11AF3F44F55176341D8"),

        new ModelDescriptor(
            "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17",
            "Zipformer английский, компактная",
            ModelKind.Streaming,
            RecognitionLanguage.English,
            $"{SherpaModels}/sherpa-onnx-streaming-zipformer-en-20M-2023-02-17.tar.bz2",
            127_887_156,
            "Компактная модель на 20M параметров, подходит для потокового ввода на любом CPU.",
            Recommended: true,
            Sha256: "9C559283E8498D3FE95913C79CA1CB454BB26281AC2B102B41306C7D752765D9"),

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
