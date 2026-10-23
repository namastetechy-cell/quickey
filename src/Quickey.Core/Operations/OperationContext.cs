using Quickey.Core.Settings;

namespace Quickey.Core.Operations;

public sealed class OperationContext(OutputLocation outputLocation = OutputLocation.SameFolder)
{
    public OutputLocation OutputLocation { get; } = outputLocation;

    /// <summary>
    /// Picks a path for an output file next to <paramref name="source"/> (or in a "Quickey" subfolder),
    /// never overwriting anything: "photo.png", then "photo (1).png", "photo (2).png"...
    /// </summary>
    public string OutputPathFor(string source, string newExtension, string? suffix = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        if (OutputLocation == OutputLocation.QuickeySubfolder)
        {
            folder = Path.Combine(folder, "Quickey");
            Directory.CreateDirectory(folder);
        }
        var baseName = Path.GetFileNameWithoutExtension(source) + (suffix ?? "");
        return UniquePath(folder, baseName, newExtension);
    }

    public static string UniquePath(string folder, string baseName, string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        var candidate = Path.Combine(folder, baseName + extension);
        for (var i = 1; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return candidate;
    }
}
