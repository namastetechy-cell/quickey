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
        var source = _temp.TextPdf("doc.pdf", "Hello Quickey", "Second line 123");

        var result = await Run("pdf.to.txt", source);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var text = File.ReadAllText(Assert.Single(result.Outputs));
        Assert.Contains("Hello Quickey", text);
        Assert.Contains("Second line 123", text);
    }

    [Fact]
    public async Task Scanned_pdf_without_ocr_explains_instead_of_writing_an_empty_file()
    {
        var scan = _temp.Pdf("scan.pdf", "white");

        var result = await Run("pdf.to.txt", scan);

        Assert.Empty(result.Outputs);
        Assert.Contains("scanned", Assert.Single(result.Errors).Message);
        Assert.False(File.Exists(_temp.File("scan.txt")));
    }

    [Fact]
    public async Task Scanned_pages_go_through_ocr_and_text_pages_do_not()
    {
        var ocr = new FakeOcr("Recognised words");
        var registry = OperationRegistry.CreateDefault(ocr: ocr);
        var scan = _temp.Pdf("scan.pdf", "white", "white");
        var text = _temp.TextPdf("text.pdf", "Real text layer");

        var scanned = await registry.Find("pdf.to.txt")!.RunAsync([scan], _context, CancellationToken.None);
        Assert.Equal(2, ocr.Calls);
        Assert.Contains("Recognised words", File.ReadAllText(Assert.Single(scanned.Outputs)));
        Assert.Contains("--- Page 2 ---", File.ReadAllText(scanned.Outputs[0]));

        await registry.Find("pdf.to.txt")!.RunAsync([text], _context, CancellationToken.None);
        Assert.Equal(2, ocr.Calls);
    }

    private sealed class FakeOcr(string text) : Quickey.Core.Text.ITextRecognizer
    {
        public int Calls { get; private set; }

        public Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken)
        {
            Assert.Equal(ImageMagick.MagickFormat.Png, new ImageMagick.MagickImageInfo(image).Format);
            Calls++;
            return Task.FromResult(text);
        }
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
