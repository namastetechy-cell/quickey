using PdfSharp.Pdf.IO;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class PdfOperationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly OperationRegistry _registry = OperationRegistry.CreateDefault();
    private readonly OperationContext _context = new();

    public void Dispose() => _temp.Dispose();

    private Task<OperationResult> Run(string id, params string[] files) =>
        _registry.Find(id)!.RunAsync(files, _context, CancellationToken.None);

    private static int Pages(string path)
    {
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return pdf.PageCount;
    }

    [Fact]
    public async Task Merges_in_drop_order()
    {
        var a = _temp.Pdf("a.pdf", "red", "green");
        var b = _temp.Pdf("b.pdf", "blue");

        var result = await Run("pdf.merge", a, b);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(_temp.File("a-merged.pdf"), output);
        Assert.Equal(3, Pages(output));
    }

    [Fact]
    public async Task Splits_into_one_file_per_page()
    {
        var source = _temp.Pdf("doc.pdf", "red", "green", "blue");

        var result = await Run("pdf.split", source);

        Assert.Equal(new[] { "doc-1.pdf", "doc-2.pdf", "doc-3.pdf" }, result.Outputs.Select(Path.GetFileName));
        Assert.All(result.Outputs, o => Assert.Equal(1, Pages(o)));
    }

    [Fact]
    public async Task Rotates_every_page()
    {
        var source = _temp.Pdf("doc.pdf", "red", "green");

        var result = await Run("pdf.rotate", source);

        using var pdf = PdfReader.Open(Assert.Single(result.Outputs), PdfDocumentOpenMode.Import);
        Assert.All(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>(), p => Assert.Equal(90, p.Rotate));
    }

    [Fact]
    public async Task Renders_each_page_to_png()
    {
        var source = _temp.Pdf("doc.pdf", "red", "green");

        var result = await Run("pdf.to.png", source);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(new[] { "doc-1.png", "doc-2.png" }, result.Outputs.Select(Path.GetFileName));
        Assert.Equal(ImageMagick.MagickFormat.Png, new ImageMagick.MagickImageInfo(result.Outputs[0]).Format);
    }

    [Fact]
    public async Task Single_page_pdf_renders_without_page_suffix()
    {
        var source = _temp.Pdf("cover.pdf", "red");

        var result = await Run("pdf.to.jpg", source);

        Assert.Equal(_temp.File("cover.jpg"), Assert.Single(result.Outputs));
    }

    [Fact]
    public async Task Extracts_text_to_a_txt_file()
    {
        var source = _temp.Pdf("doc.pdf", "white");

        var result = await Run("pdf.to.txt", source);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(_temp.File("doc.txt"), Assert.Single(result.Outputs));
    }

    [Fact]
    public void Merge_needs_at_least_two_pdfs()
    {
        var single = new[] { _temp.Pdf("a.pdf", "red") };

        Assert.DoesNotContain(_registry.For(OperationCategory.Tools, single), o => o.Id == "pdf.merge");
    }

    [Fact]
    public async Task Reports_a_corrupt_pdf_as_an_error()
    {
        var bad = _temp.File("bad.pdf");
        File.WriteAllText(bad, "not a pdf");

        var result = await Run("pdf.split", bad);

        Assert.Empty(result.Outputs);
        Assert.Single(result.Errors);
    }
}
