using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Quickey.App.Actions;
using Quickey.App.Input;
using Quickey.App.Licensing;
using Quickey.App.Native;
using Quickey.App.Office;
using Quickey.App.Services;
using Quickey.App.Shell;
using Quickey.App.UI;
using Quickey.Core.Licensing;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.App;

/// <summary>Composition root: wires the input hooks, the wheel, the runner and the tray together.</summary>
public sealed class App : Application
{
    private readonly SingleInstance _instance;
    private readonly CommandLine _startup;

    private readonly SettingsStore _settingsStore = new(AppPaths.Settings);
    private readonly UsageTracker _usage = new(AppPaths.Usage);
    private readonly List<string> _pendingFiles = [];
    private readonly DispatcherTimer _filesBatch = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _housekeeping = new() { Interval = TimeSpan.FromHours(6) };

    private AppSettings _settings = new();
    private LicenseManager _license = null!;
    private Entitlements _entitlements = null!;
    private WheelWindow _wheel = null!;
    private DragWatcher _watcher = null!;
    private TrayIcon _tray = null!;
    private OperationRunner _runner = null!;
    private UpdateService _updates = null!;
    private SettingsWindow? _settingsWindow;
    private bool _paused;

    public App(SingleInstance instance, CommandLine startup)
    {
        _instance = instance;
        _startup = startup;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info($"Quickey {Log.Version} starting on {Environment.OSVersion}");

        // Quickey runs all day in the tray: a bug in one feature must not take the whole app down
        // (and with it the drag hooks). Log it, tell the user, keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
            _tray?.Notify("Quickey hit a problem", "It's still running. Details are in the log (tray menu → Open log folder).", isError: true);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal exception", args.ExceptionObject as Exception);

        var firstRun = !File.Exists(AppPaths.Settings);
        _settings = _settingsStore.Load();
        if (firstRun)
        {
            _settingsStore.Save(_settings);
            ShellIntegration.StartsWithWindows = _settings.StartWithWindows;
            ShellIntegration.RegisterContextMenu();
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _license = new LicenseManager(new LemonSqueezyClient(http, Brand.LicenseProduct), new DpapiLicenseStore(AppPaths.License));
        _entitlements = new Entitlements(_license, _usage);

        var registry = OperationRegistry.CreateDefault()
            .AddRange(OfficeOperations.All())
            .AddRange(FileActions.All());

        _wheel = new WheelWindow(registry, () => _settings);
        _tray = new TrayIcon(OpenSettings, TogglePause, OpenCheckout, OpenLogFolder, Quit);
        _runner = new OperationRunner(_entitlements, () => _settings, _tray, OpenCheckout, RefreshStatus);
        _updates = new UpdateService(() => _settings.UpdateUrl, _tray);
        _wheel.OperationChosen += async (_, chosen) =>
        {
            Log.Info($"Run {chosen.Operation.Id} on {chosen.Files.Count} file(s)");
            await _runner.RunAsync(chosen.Operation, chosen.Files);
        };
        _license.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshStatus);

        _watcher = new DragWatcher(Dispatcher, () => _settings.ExplorerOnly) { IgnorePoint = _wheel.Contains };
        _watcher.ModifierPressed += (_, args) =>
        {
            if (_settings.CategoryFor(args.Key) is { } category)
            {
                Log.Info($"{args.Key} mid-drag → {category} wheel");
                _wheel.ShowForDrag(category, args.Cursor);
            }
        };
        _watcher.DragEnded += (_, _) => _wheel.OnDragReleased();
        _watcher.Cancelled += (_, _) => _wheel.HideWheel();
        try
        {
            _watcher.Start();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("Hooks failed", ex);
            _tray.Notify("Quickey can't watch drags", ex.Message + " The right-click menu still works.", isError: true);
        }

        // Hooks don't always survive sleep or the lock screen; reinstall them on the way back.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == Microsoft.Win32.PowerModes.Resume)
                _watcher.Reinstall();
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, args) =>
        {
            if (args.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect)
                _watcher.Reinstall();
        };

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => Handle(CommandLine.Parse(args)));
        _instance.StartListening();
        _filesBatch.Tick += (_, _) => ShowWheelForPendingFiles();

        _housekeeping.Tick += async (_, _) => await Housekeeping();
        _housekeeping.Start();
        _ = Housekeeping();

        RefreshStatus();
        if (firstRun)
            OpenSettings();
        Handle(_startup);
    }

    private void Handle(CommandLine command)
    {
        // Launched by hand (Start menu, or again while running): show settings so something visibly happens.
        if (command.OpenSettings || (command.Files.Count == 0 && !command.Background))
            OpenSettings();
        if (command.Files.Count == 0)
            return;

        // Explorer launches Quickey once per selected file; collect them for a moment into one batch.
        _pendingFiles.AddRange(command.Files.Where(f => !_pendingFiles.Contains(f, StringComparer.OrdinalIgnoreCase)));
        _filesBatch.Stop();
        _filesBatch.Start();
    }

    private void ShowWheelForPendingFiles()
    {
        _filesBatch.Stop();
        var files = _pendingFiles.ToList();
        _pendingFiles.Clear();
        Log.Info($"Right-click menu: {files.Count} file(s)");
        Win32.GetCursorPos(out var cursor);
        _wheel.ShowForFiles(files, OperationCategory.Convert, cursor);
    }

    private async Task Housekeeping()
    {
        await _license.RefreshAsync();
        await _updates.CheckAsync();
        RefreshStatus();
    }

    private void RefreshStatus() => _tray.SetStatus(_paused, _entitlements.IsPro, _usage.RemainingToday);

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _watcher.Stop();
            _wheel.HideWheel();
        }
        else
        {
            _watcher.Start();
        }
        RefreshStatus();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, SaveSettings, _license, _usage);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        _settingsStore.Save(settings);
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (File.Exists(Log.FilePath))
            TrayIcon.Reveal(Log.FilePath);
        else
            System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.Root}\"");
    }

    private void OpenCheckout()
    {
        if (Brand.CheckoutUrl != "")
            TrayIcon.OpenUrl(Brand.CheckoutUrl);
        else
            OpenSettings();
    }

    private void Quit()
    {
        Log.Info("Quit");
        _watcher.Dispose();
        _tray.Dispose();
        _updates.ApplyOnExit();
        Shutdown();
    }
}
