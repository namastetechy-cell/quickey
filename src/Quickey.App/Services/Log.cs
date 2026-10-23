using System.IO;
using System.Reflection;

namespace Quickey.App.Services;

/// <summary>
/// A small rolling text log at %LocalAppData%\Quickey\Data\quickey.log, so a problem on someone's PC
/// can be diagnosed from the file. Never throws; logging must not be what breaks the app.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.Root, "quickey.log");

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Disk full, file locked by an editor... nothing sensible to do.
        }
    }
}
