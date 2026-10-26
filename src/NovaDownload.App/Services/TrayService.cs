using System.Windows;
using NovaDownload.Core.Localization;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NovaDownload.App.Services;

/// <summary>
/// Notification-area icon. Closing or minimising the window leaves the process alive, so the icon
/// is how the user comes back — and its menu is the only way to quit while transfers still run.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _open;
    private readonly WinForms.ToolStripMenuItem _pause;
    private readonly WinForms.ToolStripMenuItem _resume;
    private readonly WinForms.ToolStripMenuItem _options;
    private readonly WinForms.ToolStripMenuItem _exit;

    private bool _disposed;

    /// <summary>Double click, or "Ouvrir": bring the window back.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>"Quitter": the shell asks for confirmation when transfers are still live.</summary>
    public event EventHandler? ExitRequested;

    public event EventHandler? PauseAllRequested;

    public event EventHandler? ResumeAllRequested;

    public event EventHandler? OptionsRequested;

    public TrayService()
    {
        _open = new WinForms.ToolStripMenuItem();
        _pause = new WinForms.ToolStripMenuItem();
        _resume = new WinForms.ToolStripMenuItem();
        _options = new WinForms.ToolStripMenuItem();
        _exit = new WinForms.ToolStripMenuItem();

        _open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _pause.Click += (_, _) => PauseAllRequested?.Invoke(this, EventArgs.Empty);
        _resume.Click += (_, _) => ResumeAllRequested?.Invoke(this, EventArgs.Empty);
        _options.Click += (_, _) => OptionsRequested?.Invoke(this, EventArgs.Empty);
        _exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_resume);
        menu.Items.Add(_pause);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_options);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_exit);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        RefreshText();
        Localizer.LanguageChanged += (_, _) => RefreshText();
    }

    /// <summary>
    /// The icon shipped with the executable, read from the assembly resources. A missing resource
    /// falls back to the stock application icon rather than leaving an invisible tray entry.
    /// </summary>
    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/novadownload.ico"));
            if (resource?.Stream != null) return new Drawing.Icon(resource.Stream);
        }
        catch (Exception)
        {
            // Falls through to the stock icon.
        }

        return Drawing.SystemIcons.Application;
    }

    private void RefreshText()
    {
        _open.Text = Localizer.Get("Tray_Open");
        _pause.Text = Localizer.Get("Tray_PauseAll");
        _resume.Text = Localizer.Get("Tray_ResumeAll");
        _options.Text = Localizer.Get("Tray_Options");
        _exit.Text = Localizer.Get("Tray_Exit");
        _icon.Text = Localizer.Get("Tray_TooltipSimple");
    }

    /// <summary>Counters in the tooltip: the pane the user sees when the window is hidden.</summary>
    public void SetSummary(int active, double bytesPerSecond)
    {
        if (_disposed) return;

        var text = active > 0
            ? Localizer.Format("Tray_TooltipActive", active, Core.Common.Formatters.Speed(bytesPerSecond))
            : Localizer.Get("Tray_TooltipSimple");

        // WinForms truncates a NotifyIcon text past 63 characters.
        _icon.Text = text.Length > 63 ? text[..60] + "…" : text;
    }

    public void NotifyCompletion(string fileName)
    {
        if (_disposed) return;

        try
        {
            _icon.ShowBalloonTip(4000, Localizer.Get("Tray_CompletedTitle"),
                Localizer.Format("Tray_Completed", fileName), WinForms.ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // The shell can refuse balloons; a missing notification never blocks a download.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
    }
}
