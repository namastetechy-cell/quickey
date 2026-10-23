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

    /// <summary>
    /// Writes a minimal PDF with a real text layer (standard Helvetica, nothing embedded), one line
    /// per entry. Built by hand so tests don't depend on installed fonts.
    /// </summary>
    public string TextPdf(string name, params string[] lines)
    {
        var text = new System.Text.StringBuilder("BT /F1 18 Tf 72 720 Td ");
        foreach (var line in lines)
            text.Append($"({line}) Tj 0 -24 Td ");
        text.Append("ET");
        var content = text.ToString();

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = File(name);
        System.IO.File.WriteAllText(path, pdf.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
