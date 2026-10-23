using Microsoft.Win32;

namespace Quickey.App.Shell;

/// <summary>
/// Per-user registry entries only (HKCU), so nothing needs admin rights and uninstalling is clean.
/// On Windows 11 the menu item appears under "Show more options".
/// </summary>
public static class ShellIntegration
{
    private const string VerbKey = @"Software\Classes\{0}\shell\Quickey";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] Targets = ["*", "Directory"];

    public static string ExecutablePath => Environment.ProcessPath!;

    public static bool IsContextMenuRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(string.Format(VerbKey, "*") + @"\command");
            return key?.GetValue(null) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void RegisterContextMenu()
    {
        foreach (var target in Targets)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(string.Format(VerbKey, target));
            verb.SetValue("MUIVerb", "Convert with Quickey");
            verb.SetValue("Icon", $"\"{ExecutablePath}\",0");
            // Show for multi-selections too; each file arrives as its own launch and is batched over IPC.
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExecutablePath}\" --files \"%1\"");
        }
    }

    public static void UnregisterContextMenu()
    {
        foreach (var target in Targets)
            Registry.CurrentUser.DeleteSubKeyTree(string.Format(VerbKey, target), throwOnMissingSubKey: false);
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Quickey") is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue("Quickey", $"\"{ExecutablePath}\" --background");
            else
                key.DeleteValue("Quickey", throwOnMissingValue: false);
        }
    }

    /// <summary>Called by the uninstaller hook so no dead entries are left behind.</summary>
    public static void RemoveAll()
    {
        UnregisterContextMenu();
        StartsWithWindows = false;
    }
}
