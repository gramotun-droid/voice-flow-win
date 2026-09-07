using System.Windows;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.App.ViewModels;

/// <summary>
/// Состояние компактного окна поверх остальных приложений.
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    private readonly DictationController _controller;
    private readonly HybridTranscriptionCoordinator _coordinator;
    private readonly ISettingsService _settings;

    private string _stableText = string.Empty;
    private string _statusText = "Ожидание";
    private string _languageText = "RU";
    private double _level;
    private bool _isDictating;
    private bool _interventionWarning;

    public OverlayViewModel(
        DictationController controller,
        HybridTranscriptionCoordinator coordinator,
        ISettingsService settings)
    {
        _controller = controller;
        _coordinator = coordinator;
        _settings = settings;

        StopCommand = new RelayCommand(() => _controller.Stop());
        CancelCommand = new RelayCommand(() => CancelCurrentSegment());

        _controller.StateChanged += OnStateChanged;
        _controller.LevelChanged += OnLevelChanged;
        _coordinator.SegmentUpdated += OnSegmentUpdated;
        _coordinator.InjectionProblem += OnInjectionProblem;
    }

    public RelayCommand StopCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string StableText
    {
        get => _stableText;
        private set => SetField(ref _stableText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string LanguageText
    {
        get => _languageText;
        private set => SetField(ref _languageText, value);
    }

    /// <summary>Уровень сигнала от 0 до 1 для индикатора микрофона.</summary>
    public double Level
    {
        get => _level;
        private set => SetField(ref _level, value);
    }

    public bool IsDictating
    {
        get => _isDictating;
        private set
        {
            if (SetField(ref _isDictating, value))
            {
                OnPropertyChanged(nameof(Visibility));
            }
        }
    }

    /// <summary>Пользователь вмешался: автоматическая замена больше не выполняется.</summary>
    public bool InterventionWarning
    {
        get => _interventionWarning;
        private set => SetField(ref _interventionWarning, value);
    }

    /// <summary>Overlay скрывается целиком, если пользователь этого захотел.</summary>
    public Visibility Visibility => _settings.Current.Overlay.Visible ? Visibility.Visible : Visibility.Collapsed;

    public bool HideText => _settings.Current.Overlay.HideText;

    private void OnStateChanged(object? sender, DictationStateEventArgs e) => RunOnUi(() =>
    {
        IsDictating = _controller.IsDictating;
        StatusText = e.State switch
        {
            DictationState.Preparing => "Подготовка моделей…",
            DictationState.Listening => "Слушаю",
            DictationState.Speaking => "Распознаю речь",
            DictationState.Error => "Ошибка: " + (e.Message ?? "неизвестная"),
            _ => "Ожидание",
        };

        if (e.State == DictationState.Idle)
        {
            StableText = string.Empty;
            InterventionWarning = false;
            Level = 0;
        }
    });

    private void OnLevelChanged(object? sender, AudioLevelInfo info) => RunOnUi(() =>
    {
        // -60 дБ считаем тишиной, 0 дБ — максимумом шкалы.
        Level = Math.Clamp((info.LevelDb + 60) / 60.0, 0, 1);
    });

    private void OnSegmentUpdated(object? sender, SegmentEventArgs e) => RunOnUi(() =>
    {
        var segment = e.Segment;
        StableText = segment.PartialText;
        LanguageText = segment.Language switch
        {
            RecognitionLanguage.Russian => "RU",
            RecognitionLanguage.English => "EN",
            _ => "AUTO",
        };

        InterventionWarning = segment.UserIntervened;
    });

    private void OnInjectionProblem(object? sender, InjectionProblemEventArgs e) => RunOnUi(() =>
    {
        StatusText = e.Message;
        InterventionWarning = true;
    });

    private void CancelCurrentSegment()
    {
        var segment = _coordinator.CurrentSegment;
        if (segment is not null)
        {
            _ = _coordinator.CancelSegmentAsync(segment.SegmentId, CancellationToken.None);
        }

        _controller.Stop();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
