namespace Quickey.App.Services;

/// <summary>Arguments Quickey understands: "--files a b c", "--background", "--settings".</summary>
public sealed record CommandLine(IReadOnlyList<string> Files, bool Background, bool OpenSettings)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        bool background = false, settings = false, readingFiles = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--files": readingFiles = true; break;
                case "--background": background = true; readingFiles = false; break;
                case "--settings": settings = true; readingFiles = false; break;
                default:
                    if (readingFiles && !arg.StartsWith("--"))
                        files.Add(arg);
                    break;
            }
        }
        return new CommandLine(files, background, settings);
    }
}
