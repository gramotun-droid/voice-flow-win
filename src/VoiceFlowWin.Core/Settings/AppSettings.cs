using VoiceFlowWin.Core.Models;

namespace VoiceFlowWin.Core.Settings;

/// <summary>Как запускается диктовка.</summary>
public enum DictationActivationMode
{
    /// <summary>Первое нажатие включает, второе выключает.</summary>
    Toggle,

    /// <summary>Диктовка идёт, пока клавиша удерживается.</summary>
    PushToTalk,
}

/// <summary>Что делает Esc с уже распознанным текстом.</summary>
public enum EscapeBehavior
{
    /// <summary>Остановить диктовку и оставить распознанное — поведение по умолчанию.</summary>
    FinalizeAndKeep,

    /// <summary>Отменить незавершённый сегмент целиком.</summary>
    CancelSegment,
}

/// <summary>Насколько «живым» должен быть потоковый ввод.</summary>
public enum LiveTextMode
{
    /// <summary>В поле уходит только стабильный префикс, хвост показывается в overlay.</summary>
    SafeStreaming,

    /// <summary>В поле уходит и хвост; при изменении гипотезы приложение стирает собственный хвост.</summary>
    MaximumLive,
}

public enum TextInjectionMode
{
    SendInput,
    Clipboard,
    AutoFallback,
    Compatibility,
}

public enum UpdateChannel
{
    Stable,
    Beta,
}

public sealed class AppSettings
{
    /// <summary>Версия схемы настроек; 0 означает файл, записанный до её появления.</summary>
    /// <remarks>
    /// Нужна, чтобы отличать значение, оставшееся от прежнего умолчания, от
    /// такого же значения, выбранного пользователем осознанно: перенос
    /// выполняется один раз, а после него выбор пользователя не трогается.
    /// </remarks>
    public int SchemaVersion { get; set; }

    public GeneralSettings General { get; set; } = new();
    public MicrophoneSettings Microphone { get; set; } = new();
    public VoskSettings Vosk { get; set; } = new();
    public WhisperSettings Whisper { get; set; } = new();
    public InjectionSettings Injection { get; set; } = new();
    public SegmentationSettings Segmentation { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class GeneralSettings
{
    public DictationActivationMode ActivationMode { get; set; } = DictationActivationMode.Toggle;

    public HotkeyDefinition Hotkey { get; set; } = HotkeyDefinition.Default;

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public LanguageSelectionMode LanguageMode { get; set; } = LanguageSelectionMode.FollowKeyboardLayout;

    public LiveTextMode LiveTextMode { get; set; } = LiveTextMode.SafeStreaming;

    public bool AutomaticPunctuation { get; set; } = true;

    public bool VoiceCommandsEnabled { get; set; } = true;

    public EscapeBehavior EscapeBehavior { get; set; } = EscapeBehavior.FinalizeAndKeep;

    /// <summary>Гасить ли Esc во время диктовки, чтобы не закрылось окно или меню активного приложения.</summary>
    public bool SuppressEscapeDuringDictation { get; set; } = true;
}

public sealed class MicrophoneSettings
{
    /// <summary>Пустая строка — устройство по умолчанию.</summary>
    public string DeviceId { get; set; } = string.Empty;

    public double GainDb { get; set; }

    public bool NoiseSuppression { get; set; }
}

public sealed class VoskSettings
{
    public string RussianModelPath { get; set; } = string.Empty;

    public string EnglishModelPath { get; set; } = string.Empty;

    /// <summary>Как часто запрашивать промежуточный результат.</summary>
    public int PartialResultIntervalMs { get; set; } = 150;

    public int StableRepeats { get; set; } = 2;

    public int StabilityDelayMs { get; set; } = 600;

    public int VolatileTailWords { get; set; } = 2;

    public StablePrefixOptionsSnapshot ToStablePrefixOptions() => new(StableRepeats, StabilityDelayMs, VolatileTailWords);
}

/// <summary>Плоское представление настроек стабилизации — удобно передавать в Core.</summary>
public readonly record struct StablePrefixOptionsSnapshot(int Repeats, int DelayMs, int TailWords);

public sealed class WhisperSettings
{
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Имя рекомендованной модели, выбранной в Model Manager.</summary>
    public string ModelId { get; set; } = "ggml-medium-q5_0";

    public int CpuThreads { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>cpu — обязательный режим; остальные backend'ы опциональны.</summary>
    public string Backend { get; set; } = "cpu";

    /// <summary>Передавать ли Whisper короткий текстовый контекст предыдущего сегмента.</summary>
    public bool UsePreviousTextContext { get; set; } = true;

    public int MaxContextWords { get; set; } = 24;

    /// <summary>Одновременно выполняется только одно тяжёлое распознавание.</summary>
    public int MaxParallelism { get; set; } = 1;
}

public sealed class InjectionSettings
{
    public TextInjectionMode Mode { get; set; } = TextInjectionMode.AutoFallback;

    public int KeystrokeDelayMs { get; set; } = 1;

    /// <summary>Пауза перед восстановлением буфера обмена после Ctrl+V.</summary>
    public int ClipboardRestoreDelayMs { get; set; } = 250;

    /// <summary>Разрешать автоматическую замену текста результатом Whisper.</summary>
    public bool SafeFinalReplacement { get; set; } = true;

    /// <summary>После вмешательства пользователя автозамена запрещена всегда.</summary>
    public bool BlockReplacementAfterIntervention { get; set; } = true;

    /// <summary>
    /// Если Whisper вернул текст, слишком не похожий на введённый, замена не
    /// выполняется: скорее всего распознан не тот сегмент.
    /// </summary>
    public double MinimumReplacementSimilarity { get; set; } = 0.35;
}

public sealed class SegmentationSettings
{
    /// <summary>Порог VAD: 0 — очень чувствительно, 1 — только громкая речь.</summary>
    public double VadSensitivity { get; set; } = 0.5;

    public int MinSpeechMs { get; set; } = 200;

    public int SilenceToEndSegmentMs { get; set; } = 800;

    public int PreRollMs { get; set; } = 300;

    public int PostRollMs { get; set; } = 200;

    public int MaxSegmentSeconds { get; set; } = 25;
}

public sealed class PrivacySettings
{
    public bool StoreAudio { get; set; }

    public bool StoreRecognizedText { get; set; }

    public bool KeepHistory { get; set; }

    public int HistoryRetentionDays { get; set; } = 7;
}

public sealed class UpdateSettings
{
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    public bool AutomaticDownload { get; set; } = true;

    public string ManifestUrl { get; set; } =
        "https://github.com/gramotun-droid/voice-flow-win/releases/latest/download/update-manifest.json";

    public string ReleasesApiUrl { get; set; } =
        "https://api.github.com/repos/gramotun-droid/voice-flow-win/releases";

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Скрытая расширенная настройка: пропущенная необязательная версия.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Интервал плановой проверки при непрерывной работе.</summary>
    public int CheckIntervalHours { get; set; } = 4;
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;

    public bool MinimalMode { get; set; }

    public bool HideText { get; set; }

    // null означает «положение ещё не выбрано»: NaN здесь не годится, потому
    // что System.Text.Json отказывается его записывать и роняет сохранение.
    public double? Left { get; set; }

    public double? Top { get; set; }
}
