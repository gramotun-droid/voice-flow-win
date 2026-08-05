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
    string? Sha256 = null)
{
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
/// </remarks>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelDescriptor> All { get; } = new[]
    {
        new ModelDescriptor(
            "vosk-model-small-ru-0.22",
            "Vosk русский, компактная",
            ModelKind.Vosk,
            RecognitionLanguage.Russian,
            "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip",
            45L * 1024 * 1024,
            "Быстрая, подходит для потокового ввода на любом CPU.",
            Recommended: true),

        new ModelDescriptor(
            "vosk-model-ru-0.42",
            "Vosk русский, полная",
            ModelKind.Vosk,
            RecognitionLanguage.Russian,
            "https://alphacephei.com/vosk/models/vosk-model-ru-0.42.zip",
            1800L * 1024 * 1024,
            "Точнее, но требует около 4 ГБ оперативной памяти.",
            Recommended: false),

        new ModelDescriptor(
            "vosk-model-small-en-us-0.15",
            "Vosk английский, компактная",
            ModelKind.Vosk,
            RecognitionLanguage.English,
            "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip",
            40L * 1024 * 1024,
            "Быстрая, подходит для потокового ввода на любом CPU.",
            Recommended: true),

        new ModelDescriptor(
            "ggml-small-q5_1",
            "Whisper small, квантованная",
            ModelKind.Whisper,
            RecognitionLanguage.Auto,
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
            190L * 1024 * 1024,
            "Мультиязычная. Финализация фразы примерно за секунду на 4 ядрах.",
            Recommended: true),

        new ModelDescriptor(
            "ggml-medium-q5_0",
            "Whisper medium, квантованная",
            ModelKind.Whisper,
            RecognitionLanguage.Auto,
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium-q5_0.bin",
            540L * 1024 * 1024,
            "Заметно точнее на терминах, требует 4+ ядер и 2 ГБ памяти.",
            Recommended: false),
    };

    public static IEnumerable<ModelDescriptor> Recommended => All.Where(model => model.Recommended);

    public static ModelDescriptor? Find(string id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
}
