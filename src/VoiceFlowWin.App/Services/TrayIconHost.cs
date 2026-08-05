using System.Drawing;
using System.IO;
using System.Windows.Forms;
using VoiceFlowWin.Core.Coordination;

namespace VoiceFlowWin.App.Services;

/// <summary>
/// Значок в области уведомлений.
/// </summary>
/// <remarks>
/// Приложение живёт в трее, а не в панели задач: диктовка вызывается горячей
/// клавишей из любого окна, и держать ради этого окно на экране незачем.
/// Значок отражает текущее состояние, чтобы было видно, слушает ли микрофон.
/// </remarks>
public sealed class TrayIconHost : IDisposable
{
    private readonly DictationController _controller;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private bool _disposed;

    public TrayIconHost(DictationController controller)
    {
        _controller = controller;

        _toggleItem = new ToolStripMenuItem("Начать диктовку", null, (_, _) => ToggleDictationRequested?.Invoke(this, EventArgs.Empty));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "VoiceFlowWin",
            ContextMenuStrip = menu,
            Visible = false,
        };

        _icon.DoubleClick += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        _controller.StateChanged += OnStateChanged;
    }

    public event EventHandler? ShowSettingsRequested;

    public event EventHandler? ToggleDictationRequested;

    public event EventHandler? ExitRequested;

    public void Start() => _icon.Visible = true;

    public void ShowMessage(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void OnStateChanged(object? sender, DictationStateEventArgs e)
    {
        var status = e.State switch
        {
            DictationState.Preparing => "Подготовка…",
            DictationState.Listening => "Слушаю",
            DictationState.Speaking => "Распознаю речь",
            DictationState.Finalizing => "Финализация",
            DictationState.Error => "Ошибка: " + (e.Message ?? "неизвестная"),
            _ => "Ожидание",
        };

        // Windows обрезает подсказку значка на 63 символах.
        var tooltip = "VoiceFlowWin — " + status;
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
        _toggleItem.Text = _controller.IsDictating ? "Остановить диктовку" : "Начать диктовку";
    }

    private static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}
