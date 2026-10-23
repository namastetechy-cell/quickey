namespace Quickey.Core.Operations;

/// <summary>Coarse file family, used to decide which operations apply to a drop.</summary>
public enum FileKind
{
    Other,
    Folder,
    Image,
    Pdf,
    Word,
    Excel,
    PowerPoint,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = FileKind.Image,
        [".jpg"] = FileKind.Image,
        [".jpeg"] = FileKind.Image,
        [".jfif"] = FileKind.Image,
        [".webp"] = FileKind.Image,
        [".heic"] = FileKind.Image,
        [".heif"] = FileKind.Image,
        [".avif"] = FileKind.Image,
        [".gif"] = FileKind.Image,
        [".bmp"] = FileKind.Image,
        [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image,
        [".ico"] = FileKind.Image,
        [".tga"] = FileKind.Image,
        [".psd"] = FileKind.Image,
        [".svg"] = FileKind.Image,

        [".pdf"] = FileKind.Pdf,

        [".doc"] = FileKind.Word,
        [".docx"] = FileKind.Word,
        [".docm"] = FileKind.Word,
        [".rtf"] = FileKind.Word,
        [".odt"] = FileKind.Word,

        [".xls"] = FileKind.Excel,
        [".xlsx"] = FileKind.Excel,
        [".xlsm"] = FileKind.Excel,
        [".ods"] = FileKind.Excel,
        [".csv"] = FileKind.Excel,

        [".ppt"] = FileKind.PowerPoint,
        [".pptx"] = FileKind.PowerPoint,
        [".pptm"] = FileKind.PowerPoint,
        [".odp"] = FileKind.PowerPoint,
    };

    public static FileKind Detect(string path)
    {
        if (Directory.Exists(path))
            return FileKind.Folder;
        return ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : FileKind.Other;
    }

    /// <summary>True when every file in the selection is of the given kind.</summary>
    public static bool All(IReadOnlyList<string> files, FileKind kind) =>
        files.Count > 0 && files.All(f => Detect(f) == kind);

    /// <summary>True when every file is one of the given kinds.</summary>
    public static bool AllIn(IReadOnlyList<string> files, params FileKind[] kinds) =>
        files.Count > 0 && files.All(f => kinds.Contains(Detect(f)));
}
