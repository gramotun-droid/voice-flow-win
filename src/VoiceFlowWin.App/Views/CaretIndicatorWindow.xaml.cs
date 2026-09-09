using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Coordination;
using VoiceFlowWin.Core.Settings;
using Color = System.Windows.Media.Color;

namespace VoiceFlowWin.App.Views;

/// <summary>Неактивируемый индикатор диктовки рядом с текстовой кареткой.</summary>
public partial class CaretIndicatorWindow : Window, IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = new(-1);

    private readonly ICaretPositionProvider _caret;
    private readonly ISettingsService _settings;
    private readonly DictationController _controller;
    private readonly DispatcherTimer _positionTimer;
    private nint _handle;
    private bool _disposed;

    public CaretIndicatorWindow(
        ICaretPositionProvider caret,
        ISettingsService settings,
        DictationController controller)
    {
        _caret = caret;
        _settings = settings;
        _controller = controller;

        InitializeComponent();

        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _positionTimer.Tick += OnPositionTick;
        _controller.StateChanged += OnStateChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public void SyncVisibility()
    {
        if (_disposed)
        {
            return;
        }

        var wanted = _settings.Current.Overlay.ShowCaretIndicator &&
            _controller.State is DictationState.Preparing or DictationState.Listening or DictationState.Speaking;

        if (!wanted)
        {
            _positionTimer.Stop();
            Hide();
            return;
        }

        UpdateVisualState();
        UpdatePosition();
        _positionTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTick;
        _controller.StateChanged -= OnStateChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        Hide();
    }

    private void OnStateChanged(object? sender, DictationStateEventArgs e) =>
        Dispatcher.BeginInvoke(SyncVisibility);

    private void OnSettingsChanged(object? sender, AppSettings e) =>
        Dispatcher.BeginInvoke(SyncVisibility);

    private void OnPositionTick(object? sender, EventArgs e) => UpdatePosition();

    private void UpdatePosition()
    {
        if (!_caret.TryGetCaretBounds(out var caret))
        {
            Hide();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        var screen = global::System.Windows.Forms.Screen.FromPoint(
            new global::System.Drawing.Point(caret.Left, caret.Bottom));
        var workArea = screen.WorkingArea;

        const int gap = 4;
        const int indicatorPixels = 32;
        var x = caret.Right + gap;
        var y = caret.Bottom + gap;

        if (x + indicatorPixels > workArea.Right)
        {
            x = caret.Left - indicatorPixels - gap;
        }

        if (y + indicatorPixels > workArea.Bottom)
        {
            y = caret.Top - indicatorPixels - gap;
        }

        x = Math.Max(workArea.Left, x);
        y = Math.Max(workArea.Top, y);

        SetWindowPos(_handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void UpdateVisualState()
    {
        Halo.Fill = _controller.State switch
        {
            DictationState.Speaking => new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            DictationState.Listening => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            _ => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        };

        if (_controller.State == DictationState.Speaking)
        {
            Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(420))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            Halo.BeginAnimation(OpacityProperty, null);
            Halo.Opacity = 1;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
