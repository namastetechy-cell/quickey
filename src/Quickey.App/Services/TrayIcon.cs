using System.Diagnostics;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Quickey.App.Services;

/// <summary>The notification-area icon: status, menu, and the balloon that reports each run.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _status = new() { Enabled = false };
    private readonly WinForms.ToolStripMenuItem _pause = new("Pause");
    private readonly WinForms.ToolStripMenuItem _getPro = new("Get Quickey Pro…");
    private Action? _balloonClick;

    public TrayIcon(Action openSettings, Action togglePause, Action getPro, Action openLog, Action quit)
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/quickey.ico"))!.Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = Brand.Name,
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        _pause.Click += (_, _) => togglePause();
        _getPro.Click += (_, _) => getPro();
        _icon.ContextMenuStrip.Items.AddRange(
        [
            _status,
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Settings…", null, (_, _) => openSettings()),
            _pause,
            _getPro,
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Open log folder", null, (_, _) => openLog()),
            new WinForms.ToolStripMenuItem("Quit Quickey", null, (_, _) => quit()),
        ]);
        _icon.DoubleClick += (_, _) => openSettings();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    public void SetStatus(bool paused, bool isPro, int remainingToday)
    {
        _pause.Text = paused ? "Resume" : "Pause";
        _getPro.Visible = !isPro && Brand.CheckoutUrl != "";
        var state = paused ? "Paused" : isPro ? "Pro" : $"Free: {remainingToday} conversions left today";
        _status.Text = $"Quickey {Log.Version} · {state}";
        // NotifyIcon tooltips are limited to 63 characters.
        var tip = $"{Brand.Name} {Log.Version} – {state}";
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    public void Notify(string title, string message, bool isError = false, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(4000, title, message, isError ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.None);
    }

    /// <summary>Opens Explorer with the first output selected.</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
