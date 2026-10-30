using Quickey.App.Services;
using Quickey.App.Shell;
using Velopack;

namespace Quickey.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: Velopack invokes the exe with special arguments during install/update/uninstall.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.RemoveAll())
            .Run();

        AppPaths.MigrateFromLegacyLocation();
        using var instance = SingleInstance.TryAcquire(args);
        if (instance is null)
            return; // Already running: arguments were handed over.

        var app = new App(instance, CommandLine.Parse(args));
        app.Run();
    }
}
