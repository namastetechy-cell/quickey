using Velopack;

namespace Quickey.App.Services;

/// <summary>
/// Background updates via Velopack. A downloaded update is applied when Quickey quits (or right away
/// if the user clicks the notification). Does nothing for dev builds or when no feed URL is set.
/// </summary>
public sealed class UpdateService(Func<string> feedUrl, TrayIcon tray)
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public async Task CheckAsync()
    {
        var url = feedUrl();
        if (string.IsNullOrWhiteSpace(url) || _pending is not null)
            return;
        try
        {
            _manager = new UpdateManager(url);
            if (!_manager.IsInstalled)
                return;
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
                return;
            await _manager.DownloadUpdatesAsync(update);
            _pending = update;
            tray.Notify("Quickey update ready", $"Version {update.TargetFullRelease.Version} installs when you quit. Click to restart now.",
                onClick: () => _manager.ApplyUpdatesAndRestart(update.TargetFullRelease));
        }
        catch (Exception)
        {
            // Offline or feed unavailable: try again next time.
        }
    }

    /// <summary>Hands a downloaded update to Velopack's updater, which installs it after this process exits.</summary>
    public void ApplyOnExit()
    {
        if (_manager is not null && _pending is not null)
            _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
    }
}
