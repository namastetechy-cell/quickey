using ImageMagick;

namespace Quickey.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("quickey-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes a gradient test image so codecs have real pixel data to chew on.</summary>
    public string Image(string name, uint width = 64, uint height = 48, bool alpha = false)
    {
        using var image = new MagickImage("gradient:red-blue", width, height);
        if (alpha)
            image.Alpha(AlphaAction.Set);
        var path = File(name);
        image.Write(path);
        return path;
    }

    /// <summary>Writes a PDF with one page per colour.</summary>
    public string Pdf(string name, params string[] pageColours)
    {
        using var pages = new MagickImageCollection();
        foreach (var colour in pageColours)
            pages.Add(new MagickImage(new MagickColor(colour), 200, 280));
        var path = File(name);
        pages.Write(path, MagickFormat.Pdf);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
