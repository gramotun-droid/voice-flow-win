namespace VoiceFlowWin.Core.Models;

/// <summary>Язык, на котором распознаётся конкретный сегмент.</summary>
public enum RecognitionLanguage
{
    Russian,
    English,

    /// <summary>Язык определяет сам Whisper; потоковая модель использует язык раскладки.</summary>
    Auto,
}

/// <summary>Как приложение выбирает язык распознавания.</summary>
public enum LanguageSelectionMode
{
    /// <summary>По раскладке активного окна — режим по умолчанию.</summary>
    FollowKeyboardLayout,
    AlwaysRussian,
    AlwaysEnglish,
    WhisperAutoDetect,
}

public static class RecognitionLanguageExtensions
{
    /// <summary>Код языка в формате, который понимают движки распознавания.</summary>
    public static string ToCode(this RecognitionLanguage language) => language switch
    {
        RecognitionLanguage.Russian => "ru",
        RecognitionLanguage.English => "en",
        _ => "auto",
    };

    public static RecognitionLanguage FromLayoutId(int keyboardLayoutId)
    {
        // Младшее слово HKL — это LANGID; 0x0419 — русский, всё остальное
        // трактуем как английский, потому что поддерживаются только два языка.
        const int RussianPrimaryLanguage = 0x19;
        var languageId = keyboardLayoutId & 0xFFFF;
        var primary = languageId & 0x3FF;
        return primary == RussianPrimaryLanguage ? RecognitionLanguage.Russian : RecognitionLanguage.English;
    }
}
