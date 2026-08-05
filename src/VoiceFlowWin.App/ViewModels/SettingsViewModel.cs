using System.Collections.ObjectModel;
using System.Windows;
using VoiceFlowWin.App.Services;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Updater;

namespace VoiceFlowWin.App.ViewModels;

/// <summary>Одна модель в списке менеджера моделей.</summary>
public sealed class ModelItemViewModel : ObservableObject
{
    private double _progress;
    private string _stage = string.Empty;
    private bool _isInstalled;

    public ModelItemViewModel(ModelDescriptor descriptor, bool isInstalled)
    {
        Descriptor = descriptor;
        _isInstalled = isInstalled;
    }

    public ModelDescriptor Descriptor { get; }

    public string DisplayName => Descriptor.DisplayName;

    public string SizeText => Descriptor.SizeText;

    public string Requirements => Descriptor.Requirements;

    public string LanguageText => Descriptor.Language switch
    {
        RecognitionLanguage.Russian => "Русский",
        RecognitionLanguage.English => "Английский",
        _ => "Мультиязычная",
    };

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetField(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => IsInstalled ? "Установлена" : "Не установлена";

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Stage
    {
        get => _stage;
        set => SetField(ref _stage, value);
    }
}

/// <summary>
/// Модель представления окна настроек.
/// </summary>
/// <remarks>
/// Правки идут по копии настроек и применяются только по кнопке «Сохранить».
/// Иначе случайное движение ползунка чувствительности VAD меняло бы поведение
/// прямо посреди диктовки. Горячая клавиша после сохранения перерегистрируется
/// без перезапуска приложения.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IDictionaryStore _dictionaryStore;
    private readonly DictionaryProcessor _dictionaryProcessor;
    private readonly DictationController _controller;
    private readonly IAudioCaptureService _capture;
    private readonly IStartupService _startup;
    private readonly ModelManager _models;
    private readonly UpdateService _updates;
    private readonly UpdateInstaller _installer;

    private CancellationTokenSource? _modelDownload;
    private string _statusMessage = string.Empty;
    private HotkeyDefinition _hotkey;
    private bool _isCapturingHotkey;
    private UserDictionaryEntry? _selectedEntry;

    public SettingsViewModel(
        ISettingsService settingsService,
        IDictionaryStore dictionaryStore,
        DictionaryProcessor dictionaryProcessor,
        DictationController controller,
        IAudioCaptureService capture,
        IStartupService startup,
        ModelManager models,
        UpdateService updates,
        UpdateInstaller installer)
    {
        _settingsService = settingsService;
        _dictionaryStore = dictionaryStore;
        _dictionaryProcessor = dictionaryProcessor;
        _controller = controller;
        _capture = capture;
        _startup = startup;
        _models = models;
        _updates = updates;
        _installer = installer;

        Draft = Clone(settingsService.Current);
        _hotkey = Draft.General.Hotkey;

        Devices = new ObservableCollection<AudioDeviceInfo>(capture.EnumerateDevices());
        DictionaryEntries = new ObservableCollection<UserDictionaryEntry>(dictionaryStore.Load());
        Models = new ObservableCollection<ModelItemViewModel>(
            ModelCatalog.All.Select(model => new ModelItemViewModel(model, models.IsInstalled(model))));

        SaveCommand = new RelayCommand(Save);
        ResetHotkeyCommand = new RelayCommand(() => Hotkey = HotkeyDefinition.Default);
        ClearHotkeyCommand = new RelayCommand(() => Hotkey = new HotkeyDefinition(0, HotkeyModifiers.None));
        TestHotkeyCommand = new RelayCommand(TestHotkey);
        AddDictionaryEntryCommand = new RelayCommand(AddDictionaryEntry);
        RemoveDictionaryEntryCommand = new RelayCommand(RemoveDictionaryEntry, () => SelectedEntry is not null);
        InstallModelCommand = new AsyncRelayCommand(InstallModelAsync);
        RemoveModelCommand = new RelayCommand(RemoveModel);
        CancelModelDownloadCommand = new RelayCommand(() => _modelDownload?.Cancel());
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        InstallUpdateCommand = new RelayCommand(InstallUpdate, () => _updates.Status.State == UpdateState.ReadyToInstall);
        PostponeUpdateCommand = new RelayCommand(() => _updates.Postpone());

        _models.Progress += OnModelProgress;
        _updates.StatusChanged += OnUpdateStatusChanged;
    }

    /// <summary>Копия настроек, с которой работает окно.</summary>
    public AppSettings Draft { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; }

    public ObservableCollection<UserDictionaryEntry> DictionaryEntries { get; }

    public ObservableCollection<ModelItemViewModel> Models { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ResetHotkeyCommand { get; }

    public RelayCommand ClearHotkeyCommand { get; }

    public RelayCommand TestHotkeyCommand { get; }

    public RelayCommand AddDictionaryEntryCommand { get; }

    public RelayCommand RemoveDictionaryEntryCommand { get; }

    public AsyncRelayCommand InstallModelCommand { get; }

    public RelayCommand RemoveModelCommand { get; }

    public RelayCommand CancelModelDownloadCommand { get; }

    public AsyncRelayCommand CheckUpdatesCommand { get; }

    public RelayCommand InstallUpdateCommand { get; }

    public RelayCommand PostponeUpdateCommand { get; }

    public string VersionText => "Версия " + App.CurrentVersion();

    public string UpdateStatusText => _updates.Status.State switch
    {
        UpdateState.Checking => "Проверка обновлений…",
        UpdateState.UpdateAvailable => $"Доступна версия {_updates.Status.AvailableVersion}",
        UpdateState.Downloading => $"Загрузка: {_updates.Status.Progress:P0}",
        UpdateState.ReadyToInstall => $"Версия {_updates.Status.AvailableVersion} готова к установке",
        UpdateState.Failed => "Ошибка обновления: " + (_updates.Status.Error ?? "неизвестная"),
        _ => "Установлена последняя версия",
    };

    public string LastCheckedText => Draft.Updates.LastCheckedAt is { } checkedAt
        ? "Последняя проверка: " + checkedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
        : "Проверка ещё не выполнялась";

    public double UpdateProgress => _updates.Status.Progress;

    public HotkeyDefinition Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetField(ref _hotkey, value))
            {
                Draft.General.Hotkey = value;
                OnPropertyChanged(nameof(HotkeyText));
            }
        }
    }

    public string HotkeyText => _hotkey.ToDisplayString();

    /// <summary>Окно перехватывает нажатия, пока идёт назначение клавиши.</summary>
    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        set => SetField(ref _isCapturingHotkey, value);
    }

    public UserDictionaryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                RemoveDictionaryEntryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ModelItemViewModel? SelectedModel { get; set; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>Применяет назначенное сочетание, если оно допустимо.</summary>
    public bool TryAssignHotkey(int virtualKey, HotkeyModifiers modifiers)
    {
        if (virtualKey == HotkeyDefinition.VirtualKeyEscape && modifiers == HotkeyModifiers.None)
        {
            // Esc зарезервирован за остановкой диктовки и не назначается.
            StatusMessage = "Esc зарезервирован для остановки диктовки и не может быть клавишей вызова.";
            return false;
        }

        Hotkey = new HotkeyDefinition(virtualKey, modifiers);
        StatusMessage = "Назначено: " + HotkeyText;
        return true;
    }

    private void Save()
    {
        if (!Draft.General.Hotkey.IsAllowedAsPrimary())
        {
            StatusMessage = "Выберите допустимое сочетание клавиш.";
            return;
        }

        _dictionaryStore.Save(DictionaryEntries);
        _dictionaryProcessor.Reload(DictionaryEntries);

        _settingsService.Save(Draft);
        _startup.SetEnabled(Draft.General.StartWithWindows);

        StatusMessage = _controller.ApplyHotkeySettings()
            ? "Настройки сохранены."
            : "Настройки сохранены, но сочетание занято другим приложением.";
    }

    private void TestHotkey() =>
        StatusMessage = _controller.ApplyHotkeySettings()
            ? $"Сочетание {HotkeyText} свободно и зарегистрировано."
            : $"Сочетание {HotkeyText} занято другим приложением. Выберите другое.";

    private void AddDictionaryEntry()
    {
        var entry = new UserDictionaryEntry { SpokenForm = "новое слово", Replacement = "Замена" };
        DictionaryEntries.Add(entry);
        SelectedEntry = entry;
    }

    private void RemoveDictionaryEntry()
    {
        if (SelectedEntry is not null)
        {
            DictionaryEntries.Remove(SelectedEntry);
            SelectedEntry = null;
        }
    }

    public void ImportDictionary(string path)
    {
        try
        {
            var imported = _dictionaryStore.Import(path, replaceExisting: false);
            DictionaryEntries.Clear();
            foreach (var entry in imported)
            {
                DictionaryEntries.Add(entry);
            }

            _dictionaryProcessor.Reload(imported);
            StatusMessage = $"Импортировано записей: {imported.Count}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Не удалось импортировать словарь: " + ex.Message;
        }
    }

    public void ExportDictionary(string path)
    {
        try
        {
            _dictionaryStore.Save(DictionaryEntries);
            _dictionaryStore.Export(path);
            StatusMessage = "Словарь экспортирован.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Не удалось экспортировать словарь: " + ex.Message;
        }
    }

    private async Task InstallModelAsync(object? parameter)
    {
        var item = parameter as ModelItemViewModel ?? SelectedModel;
        if (item is null)
        {
            StatusMessage = "Выберите модель в списке.";
            return;
        }

        _modelDownload?.Dispose();
        _modelDownload = new CancellationTokenSource();

        try
        {
            var result = await _models.InstallAsync(item.Descriptor, _modelDownload.Token);
            if (!result.Success)
            {
                StatusMessage = "Не удалось установить модель: " + result.Error;
                return;
            }

            item.IsInstalled = true;
            ApplyModelPath(item.Descriptor, result.Path!);
            StatusMessage = $"Модель «{item.DisplayName}» установлена.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Загрузка модели отменена.";
        }
        finally
        {
            item.Progress = 0;
            item.Stage = string.Empty;
        }
    }

    /// <summary>Установленная модель сразу прописывается в настройки нужного движка.</summary>
    /// <remarks>
    /// Путь пишется и в черновик, и в сохранённые настройки. Иначе скачанная
    /// модель не работала бы до нажатия «Сохранить», а закрытие окна теряло бы
    /// её — диктовка падала с «Не задан путь к модели». Сохраняется только путь
    /// модели: прочие правки черновика остаются несохранёнными, как и ожидает
    /// пользователь.
    /// </remarks>
    private void ApplyModelPath(ModelDescriptor descriptor, string path)
    {
        var persisted = Clone(_settingsService.Current);

        if (descriptor.Kind == ModelKind.Whisper)
        {
            Draft.Whisper.ModelPath = path;
            Draft.Whisper.ModelId = descriptor.Id;
            persisted.Whisper.ModelPath = path;
            persisted.Whisper.ModelId = descriptor.Id;
        }
        else if (descriptor.Language == RecognitionLanguage.English)
        {
            Draft.Vosk.EnglishModelPath = path;
            persisted.Vosk.EnglishModelPath = path;
        }
        else
        {
            Draft.Vosk.RussianModelPath = path;
            persisted.Vosk.RussianModelPath = path;
        }

        _settingsService.Save(persisted);
        OnPropertyChanged(nameof(Draft));
    }

    private void RemoveModel()
    {
        if (SelectedModel is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"Удалить модель «{SelectedModel.DisplayName}»? Её придётся скачивать заново.",
            "VoiceFlowWin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (_models.Remove(SelectedModel.Descriptor))
        {
            SelectedModel.IsInstalled = false;

            // Путь удалённой модели убирается из настроек сразу: иначе движок
            // при следующей диктовке упёрся бы в несуществующий каталог.
            var persisted = Clone(_settingsService.Current);
            if (_models.SynchronizeInstalledPaths(persisted))
            {
                _settingsService.Save(persisted);
            }

            _models.SynchronizeInstalledPaths(Draft);
            OnPropertyChanged(nameof(Draft));
            StatusMessage = "Модель удалена.";
        }
    }

    private async Task CheckUpdatesAsync()
    {
        await _updates.CheckNowAsync(CancellationToken.None);
        Draft.Updates.LastCheckedAt = _settingsService.Current.Updates.LastCheckedAt;
        OnPropertyChanged(nameof(LastCheckedText));
    }

    private void InstallUpdate()
    {
        if (!_installer.CanInstallNow)
        {
            var answer = MessageBox.Show(
                "Идёт диктовка. Завершить её и установить обновление?",
                "VoiceFlowWin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (_installer.StartInstallation(finishDictationFirst: true))
        {
            Application.Current.Shutdown();
        }
        else
        {
            StatusMessage = "Не удалось запустить установку. Текущая версия продолжает работать.";
        }
    }

    private void OnModelProgress(object? sender, ModelProgressEventArgs e)
    {
        var item = Models.FirstOrDefault(model => model.Descriptor.Id == e.ModelId);
        if (item is null)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            item.Progress = e.Fraction;
            item.Stage = e.Stage;
        });
    }

    private void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(UpdateProgress));
            InstallUpdateCommand.RaiseCanExecuteChanged();
        });

    private static AppSettings Clone(AppSettings source) => new()
    {
        // Версию схемы обязательно переносить: без неё сохранение из окна
        // настроек выглядело бы как файл прежней версии и перенос повторился бы.
        SchemaVersion = source.SchemaVersion,
        General = new GeneralSettings
        {
            ActivationMode = source.General.ActivationMode,
            Hotkey = source.General.Hotkey,
            StartWithWindows = source.General.StartWithWindows,
            StartMinimized = source.General.StartMinimized,
            LanguageMode = source.General.LanguageMode,
            LiveTextMode = source.General.LiveTextMode,
            AutomaticPunctuation = source.General.AutomaticPunctuation,
            VoiceCommandsEnabled = source.General.VoiceCommandsEnabled,
            EscapeBehavior = source.General.EscapeBehavior,
            SuppressEscapeDuringDictation = source.General.SuppressEscapeDuringDictation,
        },
        Microphone = new MicrophoneSettings
        {
            DeviceId = source.Microphone.DeviceId,
            GainDb = source.Microphone.GainDb,
            NoiseSuppression = source.Microphone.NoiseSuppression,
        },
        Vosk = new VoskSettings
        {
            RussianModelPath = source.Vosk.RussianModelPath,
            EnglishModelPath = source.Vosk.EnglishModelPath,
            PartialResultIntervalMs = source.Vosk.PartialResultIntervalMs,
            StableRepeats = source.Vosk.StableRepeats,
            StabilityDelayMs = source.Vosk.StabilityDelayMs,
            VolatileTailWords = source.Vosk.VolatileTailWords,
        },
        Whisper = new WhisperSettings
        {
            ModelPath = source.Whisper.ModelPath,
            ModelId = source.Whisper.ModelId,
            CpuThreads = source.Whisper.CpuThreads,
            Backend = source.Whisper.Backend,
            UsePreviousTextContext = source.Whisper.UsePreviousTextContext,
            MaxContextWords = source.Whisper.MaxContextWords,
            MaxParallelism = source.Whisper.MaxParallelism,
        },
        Injection = new InjectionSettings
        {
            Mode = source.Injection.Mode,
            KeystrokeDelayMs = source.Injection.KeystrokeDelayMs,
            ClipboardRestoreDelayMs = source.Injection.ClipboardRestoreDelayMs,
            SafeFinalReplacement = source.Injection.SafeFinalReplacement,
            BlockReplacementAfterIntervention = source.Injection.BlockReplacementAfterIntervention,
            MinimumReplacementSimilarity = source.Injection.MinimumReplacementSimilarity,
        },
        Segmentation = new SegmentationSettings
        {
            VadSensitivity = source.Segmentation.VadSensitivity,
            MinSpeechMs = source.Segmentation.MinSpeechMs,
            SilenceToEndSegmentMs = source.Segmentation.SilenceToEndSegmentMs,
            PreRollMs = source.Segmentation.PreRollMs,
            PostRollMs = source.Segmentation.PostRollMs,
            MaxSegmentSeconds = source.Segmentation.MaxSegmentSeconds,
        },
        Privacy = new PrivacySettings
        {
            StoreAudio = source.Privacy.StoreAudio,
            StoreRecognizedText = source.Privacy.StoreRecognizedText,
            KeepHistory = source.Privacy.KeepHistory,
            HistoryRetentionDays = source.Privacy.HistoryRetentionDays,
        },
        Updates = new UpdateSettings
        {
            Channel = source.Updates.Channel,
            AutomaticDownload = source.Updates.AutomaticDownload,
            ManifestUrl = source.Updates.ManifestUrl,
            ReleasesApiUrl = source.Updates.ReleasesApiUrl,
            LastCheckedAt = source.Updates.LastCheckedAt,
            SkippedVersion = source.Updates.SkippedVersion,
            CheckIntervalHours = source.Updates.CheckIntervalHours,
        },
        Overlay = new OverlaySettings
        {
            Visible = source.Overlay.Visible,
            MinimalMode = source.Overlay.MinimalMode,
            HideText = source.Overlay.HideText,
            Left = source.Overlay.Left,
            Top = source.Overlay.Top,
        },
    };
}
