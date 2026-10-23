using System.IO;
using Quickey.Core.Licensing;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.App.Services;

/// <summary>Runs a chosen operation: checks the free allowance, runs it off the UI thread, reports back in the tray.</summary>
public sealed class OperationRunner(Entitlements entitlements, Func<AppSettings> settings, TrayIcon tray, Action showUpgrade, Action statusChanged)
{
    public async Task RunAsync(IOperation operation, IReadOnlyList<string> files)
    {
        if (!entitlements.CanRun(operation, files.Count))
        {
            var left = entitlements.RemainingToday;
            tray.Notify("Daily limit reached",
                left == 0
                    ? $"The free version converts {UsageTracker.FreeDailyConversions} files a day. Click to get Quickey Pro for unlimited conversions."
                    : $"Only {left} free conversions left today, and this needs {files.Count}. Click to get Quickey Pro.",
                isError: true, onClick: showUpgrade);
            return;
        }

        OperationResult result;
        try
        {
            result = await operation.RunAsync(files, new OperationContext(settings().OutputLocation), CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = OperationResult.Failed(operation.Label, files[0], ex.Message);
        }

        entitlements.Charge(operation, files.Count - result.Errors.Select(e => e.File).Distinct().Count());
        statusChanged();
        Report(operation, files, result);
    }

    private void Report(IOperation operation, IReadOnlyList<string> files, OperationResult result)
    {
        // Actions (copy path, etc.) speak for themselves; only report their failures.
        if (operation.Category == OperationCategory.Actions && result.Succeeded)
            return;
        if (!settings().ShowNotifications && result.Succeeded)
            return;

        var first = result.Outputs.FirstOrDefault();
        if (result.Succeeded)
        {
            var what = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";
            tray.Notify($"{operation.Label} done", $"{what} → {Describe(result.Outputs)}. Click to show.",
                onClick: first is null ? null : () => TrayIcon.Reveal(first));
        }
        else
        {
            var error = result.Errors[0];
            var more = result.Errors.Count > 1 ? $" (+{result.Errors.Count - 1} more)" : "";
            tray.Notify($"{operation.Label} failed for {Path.GetFileName(error.File)}{more}", error.Message, isError: true,
                onClick: first is null ? null : () => TrayIcon.Reveal(first));
        }
    }

    private static string Describe(IReadOnlyList<string> outputs) =>
        outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} files";
}
