using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoiceFlowWin.App.Services;
using VoiceFlowWin.App.ViewModels;
using VoiceFlowWin.App.Views;
using VoiceFlowWin.Audio;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Commands;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Dictionary;
using VoiceFlowWin.Core.Models;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Updater;
using VoiceFlowWin.VoskEngine;
using VoiceFlowWin.WhisperEngine;
using VoiceFlowWin.Windows.Audio;
using VoiceFlowWin.Windows.Input;
using VoiceFlowWin.Windows.System;

namespace VoiceFlowWin.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\VoiceFlowWin.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;
    private TrayIconHost? _tray;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Второй экземпляр не нужен: два процесса конкурировали бы за
        // горячую клавишу, микрофон и текстовое поле пользователя.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "VoiceFlowWin уже запущен. Значок приложения находится в области уведомлений.",
                "VoiceFlowWin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = BuildServices();

        var paths = _services.GetRequiredService<AppPaths>();
        paths.EnsureCreated();

        var settings = _services.GetRequiredService<ISettingsService>();
        settings.Load();

        var dictionary = _services.GetRequiredService<IDictionaryStore>();
        _services.GetRequiredService<DictionaryProcessor>().Reload(dictionary.Load());

        var controller = _services.GetRequiredService<DictationController>();
        if (!controller.ApplyHotkeySettings())
        {
            MessageBox.Show(
                $"Не удалось зарегистрировать сочетание {settings.Current.General.Hotkey.ToDisplayString()}: оно занято другим приложением. " +
                "Выберите другое сочетание в настройках.",
                "VoiceFlowWin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _overlay = _services.GetRequiredService<OverlayWindow>();
        _overlay.Show();

        _tray = _services.GetRequiredService<TrayIconHost>();
        _tray.ShowSettingsRequested += (_, _) => ShowSettings();
        _tray.ToggleDictationRequested += (_, _) => controller.Toggle();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.Start();

        _services.GetRequiredService<UpdateService>().Start();

        var startMinimized = settings.Current.General.StartMinimized || e.Args.Contains("--minimized");
        if (!startMinimized)
        {
            ShowSettings();
        }

        if (RequiresFirstRunSetup(settings.Current))
        {
            ShowSettings(openModelsTab: true);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowSettings(bool openModelsTab = false)
    {
        if (_services is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = _services.GetRequiredService<SettingsWindow>();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }

        if (openModelsTab)
        {
            _settingsWindow.SelectModelsTab();
        }
    }

    /// <summary>Без моделей приложение работать не может — предлагаем скачать их сразу.</summary>
    private static bool RequiresFirstRunSetup(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Vosk.RussianModelPath) || string.IsNullOrWhiteSpace(settings.Whisper.ModelPath);

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var paths = new AppPaths();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            // Технический лог не содержит содержимого диктовки: в него пишутся
            // только состояния и ошибки.
            builder.AddProvider(new FileLoggerProvider(Path.Combine(paths.LogsDirectory, "voiceflowwin.log")));
        });

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDictionaryStore, DictionaryStore>();
        services.AddSingleton<DictionaryProcessor>();
        services.AddSingleton<VoiceCommandProcessor>();

        services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceFlowWin/" + CurrentVersion());
            return client;
        });

        // Платформенный слой Windows.
        services.AddSingleton<LowLevelKeyboardHook>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<IPrivilegeLevelDetector, PrivilegeLevelDetector>();
        services.AddSingleton<IFocusTracker, ForegroundFocusTracker>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<ITextInjectionService, TextInjectionService>();
        services.AddSingleton<IInputInterventionMonitor, InputInterventionMonitor>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IEscapeStopService, EscapeStopService>();
        services.AddSingleton<IAudioCaptureService, WasapiAudioCaptureService>();

        // Аудио и распознавание.
        services.AddSingleton<IVoiceActivityDetector>(provider =>
            new EnergyVoiceActivityDetector(provider.GetRequiredService<ISettingsService>().Current.Segmentation.VadSensitivity));
        services.AddSingleton<ISpeechSegmenter>(provider => new SpeechSegmenter(
            provider.GetRequiredService<IVoiceActivityDetector>(),
            provider.GetRequiredService<ISettingsService>().Current.Segmentation));

        services.AddSingleton<VoskModelLoader>();
        services.AddSingleton<IStreamingRecognizer, StreamingVoskRecognizer>();
        services.AddSingleton<IFinalRecognizer, WhisperFinalRecognizer>();
        services.AddSingleton<WhisperTranscriptionQueue>();
        services.AddSingleton<IFinalRecognitionQueue>(provider => provider.GetRequiredService<WhisperTranscriptionQueue>());

        services.AddSingleton<HybridTranscriptionCoordinator>();
        services.AddSingleton<DictationController>();

        // Обновления и модели.
        services.AddSingleton<PackageVerifier>();
        services.AddSingleton<UpdateDownloader>();
        services.AddSingleton(provider => new UpdateService(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<UpdateDownloader>(),
            provider.GetRequiredService<PackageVerifier>(),
            provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<AppPaths>(),
            CurrentVersion(),
            provider.GetRequiredService<ILogger<UpdateService>>()));
        services.AddSingleton<ModelManager>();
        services.AddSingleton<UpdateInstaller>();

        // Интерфейс.
        services.AddSingleton<TrayIconHost>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }

    internal static SemanticVersion CurrentVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (SemanticVersion.TryParse(informational, out var version))
        {
            return version;
        }

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion is null
            ? SemanticVersion.Zero
            : new SemanticVersion(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<ILogger<App>>()?.LogError(e.Exception, "Необработанная ошибка интерфейса.");

        // Ошибка отрисовки или окна не должна выбрасывать пользователя из
        // приложения: диктовка продолжает работать из трея.
        MessageBox.Show(
            "Произошла ошибка: " + e.Exception.Message,
            "VoiceFlowWin",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }
}
