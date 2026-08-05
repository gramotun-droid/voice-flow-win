using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using VoiceFlowWin.App.ViewModels;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.App.Views;

/// <summary>
/// Компактное окно поверх остальных приложений.
/// </summary>
/// <remarks>
/// Окно принципиально не забирает фокус: иначе оно перехватывало бы каретку у
/// поля, в которое идёт диктовка, и ломало бы сам смысл потокового ввода.
/// Для этого выставляются WS_EX_NOACTIVATE и WS_EX_TOOLWINDOW — последний
/// заодно убирает окно из Alt+Tab.
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly OverlayViewModel _viewModel;
    private readonly ISettingsService _settings;
    private readonly DictationController _controller;

    public OverlayWindow(OverlayViewModel viewModel, ISettingsService settings, DictationController controller)
    {
        _viewModel = viewModel;
        _settings = settings;
        _controller = controller;

        InitializeComponent();
        DataContext = viewModel;

        RestorePosition();
        _controller.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateIndicator);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        UpdateIndicator();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Перетаскивание за любое место окна: заголовка у него нет.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            SavePosition();
        }
    }

    private void RestorePosition()
    {
        var overlay = _settings.Current.Overlay;
        var workArea = SystemParameters.WorkArea;

        if (double.IsNaN(overlay.Left) || double.IsNaN(overlay.Top))
        {
            Left = workArea.Right - Width - 24;
            Top = workArea.Bottom - 220;
            return;
        }

        // Окно могло остаться за пределами экрана после смены разрешения.
        Left = Math.Clamp(overlay.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(overlay.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 100));
    }

    private void SavePosition()
    {
        var settings = _settings.Current;
        settings.Overlay.Left = Left;
        settings.Overlay.Top = Top;
        _settings.Save(settings);
    }

    private void UpdateIndicator()
    {
        MicIndicator.Fill = _controller.State switch
        {
            DictationState.Listening => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            DictationState.Speaking => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            DictationState.Finalizing => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            DictationState.Error => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        };

        TextArea.Visibility = _viewModel.HideText ? Visibility.Collapsed : Visibility.Visible;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
