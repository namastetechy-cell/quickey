using System.Text;

namespace Quickey.Core.Tests;

/// <summary>
/// Writes small, valid PDFs by hand: standard fonts (nothing embedded) and optional JPEG images,
/// so tests control exactly what text, styles and positions a PDF contains.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly List<byte[]> _objects = [];
    private readonly StringBuilder _content = new();
    private readonly List<(string Name, int Object)> _images = [];

    // Objects 1-2 are catalog and pages, 3 the page, 4 the content, 5-7 fonts; images follow.
    private const string Fonts = "/F1 5 0 R /F2 6 0 R /F3 7 0 R";

    /// <summary>Text at (x, y) in points from the bottom-left. Font 1 regular, 2 bold, 3 italic.</summary>
    public PdfBuilder Text(double x, double y, string text, double size = 11, int font = 1)
    {
        _content.Append($"BT /F{font} {size} Tf {x} {y} Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n");
        return this;
    }

    public PdfBuilder Jpeg(byte[] jpeg, int pixelWidth, int pixelHeight, double x, double y, double width, double height)
    {
        var name = $"Im{_images.Count + 1}";
        _images.Add((name, 8 + _images.Count));
        _objects.Add(Stream($"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));
        _content.Append($"q {width} 0 0 {height} {x} {y} cm /{name} Do Q\n");
        return this;
    }

    public void Save(string path)
    {
        var xobjects = _images.Count == 0 ? "" : "/XObject << " + string.Join(" ", _images.Select(i => $"/{i.Name} {i.Object} 0 R")) + " >>";
        var header = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << {Fonts} >> {xobjects} >> /Contents 4 0 R >>"),
            Stream("<<", Encoding.ASCII.GetBytes(_content.ToString())),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique >>"),
        };
        var all = header.Concat(_objects).ToList();

        using var output = new MemoryStream();
        Write(output, "%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < all.Count; i++)
        {
            offsets.Add(output.Position);
            Write(output, $"{i + 1} 0 obj\n");
            output.Write(all[i]);
            Write(output, "\nendobj\n");
        }
        var xref = output.Position;
        Write(output, $"xref\n0 {all.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write(output, $"{offset:D10} 00000 n \n");
        Write(output, $"trailer\n<< /Size {all.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, output.ToArray());
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Stream(string dictionaryStart, byte[] data)
    {
        using var stream = new MemoryStream();
        Write(stream, $"{dictionaryStart} /Length {data.Length} >>\nstream\n");
        stream.Write(data);
        Write(stream, "\nendstream");
        return stream.ToArray();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
}
