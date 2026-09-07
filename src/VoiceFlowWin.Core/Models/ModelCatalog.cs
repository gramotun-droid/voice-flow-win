namespace VoiceFlowWin.Core.Models;

public enum ModelKind
{
    /// <summary>Потоковая модель распознавания (sherpa-onnx, Zipformer).</summary>
    Streaming,

    /// <summary>Устаревшая модель финальной проверки фразы (оставлена для чтения старых настроек).</summary>
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

    /// <summary>Потоковые модели распространяются архивами.</summary>
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
/// запуске предлагается скачать текущий набор — русскую и английскую модели
/// Zipformer.
///
/// Подмену или повреждение содержимого исключает контрольная сумма, которая
/// проверяется после каждой загрузки.
/// </remarks>
public static class ModelCatalog
{
    /// <summary>Модели sherpa-onnx лежат в релизе проекта на GitHub.</summary>
    /// <remarks>
    /// GitHub отдаёт их быстро и без зеркал, в отличие от huggingface.co: у
    /// части провайдеров тот выдаёт около килобайта в секунду.
    /// </remarks>
    private const string SherpaModels = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models";

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
    };

    public static IEnumerable<ModelDescriptor> Recommended => All.Where(model => model.Recommended);

    public static ModelDescriptor? Find(string id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
}
