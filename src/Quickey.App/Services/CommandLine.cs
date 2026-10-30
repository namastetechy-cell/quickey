namespace Quickey.App.Services;

/// <summary>Arguments Quickey understands: "--files a b c", "--background", "--settings", and "--quit-for-update" (sent by a newer version).</summary>
public sealed record CommandLine(IReadOnlyList<string> Files, bool Background, bool OpenSettings, bool QuitForUpdate = false)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        bool background = false, settings = false, quit = false, readingFiles = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--files": readingFiles = true; break;
                case "--background": background = true; readingFiles = false; break;
                case "--settings": settings = true; readingFiles = false; break;
                case SingleInstance.QuitForUpdateArgument: quit = true; readingFiles = false; break;
                default:
                    if (readingFiles && !arg.StartsWith("--"))
                        files.Add(arg);
                    break;
            }
        }
        return new CommandLine(files, background, settings, quit);
    }
}
