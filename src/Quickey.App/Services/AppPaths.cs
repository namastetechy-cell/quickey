using System.IO;

namespace Quickey.App.Services;

/// <summary>
/// Quickey's own files (settings, license, usage, log) live in %AppData%\Quickey, outside the
/// install folder. The installer replaces %LocalAppData%\Quickey wholesale, so data kept there was
/// lost on reinstall, and an Explorer window open on it (e.g. "Open log folder") made Setup fail
/// with "Failed to remove existing application directory".
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quickey");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Usage => Path.Combine(Root, "usage.json");
    public static string License => Path.Combine(Root, "license.bin");

    /// <summary>Where versions up to 0.1.9 kept their data, inside the install folder.</summary>
    private static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quickey", "Data");

    /// <summary>
    /// Copies data from the old location the first time a newer version runs. The old folder is
    /// left for the installer to remove; nothing here may stop Quickey from starting.
    /// </summary>
    public static void MigrateFromLegacyLocation()
    {
        try
        {
            if (!Directory.Exists(LegacyRoot))
                return;
            Directory.CreateDirectory(Root);
            foreach (var file in Directory.GetFiles(LegacyRoot))
            {
                var destination = Path.Combine(Root, Path.GetFileName(file));
                if (!File.Exists(destination))
                    File.Copy(file, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
