using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VoiceFlowWin.App.ViewModels;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Updater;

namespace VoiceFlowWin.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly AppPaths _paths;
    private readonly UpdateService _updates;

    public SettingsWindow(SettingsViewModel viewModel, AppPaths paths, UpdateService updates)
    {
        _viewModel = viewModel;
        _paths = paths;
        _updates = updates;

        InitializeComponent();
        DataContext = viewModel;
    }

    public void SelectModelsTab() => Tabs.SelectedItem = ModelsTab;

    /// <summary>
    /// Окно создаётся заново при каждом открытии, поэтому его модель обязана
    /// отписаться от событий приложения: иначе закрытые окна продолжали бы
    /// обрабатывать загрузку моделей и проверку обновлений.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Захват сочетания клавиш.
    /// </summary>
    /// <remarks>
    /// Сохраняется virtual key code, а не символ: иначе назначенное на русской
    /// раскладке сочетание «переехало» бы на другую клавишу при переключении
    /// на английскую. Одни модификаторы без основной клавиши не принимаются.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_viewModel.IsCapturingHotkey)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            // Ждём основную клавишу — модификатор сам по себе сочетанием не является.
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (_viewModel.TryAssignHotkey(virtualKey, modifiers))
        {
            _viewModel.IsCapturingHotkey = false;
        }
    }

    private void OnAssignHotkeyClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsCapturingHotkey = true;
        Keyboard.Focus(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnImportDictionaryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Файлы JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Импорт словаря",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ImportDictionary(dialog.FileName);
        }
    }

    private void OnExportDictionaryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Файлы JSON (*.json)|*.json",
            FileName = "voiceflowwin-dictionary.json",
            Title = "Экспорт словаря",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ExportDictionary(dialog.FileName);
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        _paths.EnsureCreated();
        Process.Start(new ProcessStartInfo(_paths.Root) { UseShellExecute = true });
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "Удалить сохранённую историю диктовок?",
            "VoiceFlowWin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (File.Exists(_paths.HistoryFile))
            {
                File.Delete(_paths.HistoryFile);
            }

            MessageBox.Show("История удалена.", "VoiceFlowWin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (IOException ex)
        {
            MessageBox.Show("Не удалось удалить историю: " + ex.Message, "VoiceFlowWin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        var url = _updates.Status.ReleaseNotesUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://github.com/gramotun-droid/voice-flow-win/releases";
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
