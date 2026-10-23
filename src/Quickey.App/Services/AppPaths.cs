using System.IO;

namespace Quickey.App.Services;

/// <summary>Everything Quickey writes lives in %LocalAppData%\Quickey\Data, beside the Velopack install.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quickey", "Data");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Usage => Path.Combine(Root, "usage.json");
    public static string License => Path.Combine(Root, "license.bin");
}
