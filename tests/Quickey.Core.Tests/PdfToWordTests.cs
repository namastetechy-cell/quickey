using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ImageMagick;
using Quickey.Core.Documents;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class PdfToWordTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Report()
    {
        using var photo = new MagickImage("plasma:", 120, 80) { Quality = 80 };
        var jpeg = photo.ToByteArray(MagickFormat.Jpeg);

        var path = _temp.File("report.pdf");
        new PdfBuilder()
            .Text(72, 720, "Quarterly Report", size: 24, font: 2)
            .Text(72, 690, "Prepared for the board", font: 3)
            .Text(72, 660, "Revenue grew in every region this quarter and costs stayed flat across the")
            .Text(72, 646, "whole business, which is the best result we have had in three years.")
            .Jpeg(jpeg, 120, 80, 72, 520, 240, 160)
            .Text(72, 480, "Item", font: 2).Text(300, 480, "Amount", font: 2)
            .Text(72, 466, "Rent").Text(300, 466, "1,200")
            .Save(path);
        return path;
    }

    private static async Task<WordprocessingDocument> Convert(string pdf, Text.ITextRecognizer? ocr = null)
    {
        var result = await new PdfToWordOperation(ocr).RunAsync([pdf], new OperationContext(), CancellationToken.None);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WordprocessingDocument.Open(Assert.Single(result.Outputs), false);
    }

    [Fact]
    public async Task Keeps_text_in_reading_order_as_editable_paragraphs()
    {
        using var docx = await Convert(Report());
        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain("%PDF", text);
        var order = new[] { "Quarterly Report", "Prepared for the board", "Revenue grew", "three years", "Rent" }.Select(s => text.IndexOf(s)).ToList();
        Assert.All(order, i => Assert.True(i >= 0, text));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public async Task Wrapped_lines_become_one_paragraph()
    {
        using var docx = await Convert(Report());

        var paragraph = docx.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Single(p => p.InnerText.Contains("Revenue grew"));
        Assert.Contains("three years", paragraph.InnerText);
        Assert.Contains("across the whole", paragraph.InnerText);
    }

    [Fact]
    public async Task Keeps_heading_size_bold_and_italic()
    {
        using var docx = await Convert(Report());
        var runs = docx.MainDocumentPart!.Document!.Body!.Descendants<Run>().ToList();

        var heading = runs.First(r => r.InnerText.Contains("Quarterly"));
        Assert.NotNull(heading.RunProperties!.Bold);
        Assert.Equal("48", heading.RunProperties.FontSize!.Val!.Value);
        Assert.NotNull(runs.First(r => r.InnerText.Contains("board")).RunProperties!.Italic);
        Assert.Null(runs.First(r => r.InnerText.Contains("Revenue")).RunProperties!.Bold);
    }

    [Fact]
    public async Task Columns_are_separated_by_tabs()
    {
        using var docx = await Convert(Report());

        var row = docx.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First(p => p.InnerText.Contains("Rent"));
        Assert.Contains("Rent\t1,200", row.InnerText);
    }

    [Fact]
    public async Task Embedded_images_are_kept()
    {
        using var docx = await Convert(Report());

        Assert.Single(docx.MainDocumentPart!.ImageParts);
        Assert.Single(docx.MainDocumentPart.Document!.Body!.Descendants<Drawing>());
    }

    [Fact]
    public async Task Scanned_pages_use_ocr_when_available()
    {
        var scan = _temp.Pdf("scan.pdf", "white");

        using var docx = await Convert(scan, new FakeOcr("Certificate of completion\nIssued 2026"));

        var text = docx.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("Certificate of completion", text);
        Assert.Contains("Issued 2026", text);
        Assert.Empty(docx.MainDocumentPart.ImageParts);
    }

    [Fact]
    public async Task Scanned_pages_without_ocr_become_a_picture()
    {
        var scan = _temp.Pdf("scan.pdf", "white", "white");

        using var docx = await Convert(scan);

        Assert.Equal(2, docx.MainDocumentPart!.ImageParts.Count());
    }

    [Fact]
    public async Task Works_for_the_text_pdf_word_itself_would_produce()
    {
        using var docx = await Convert(_temp.TextPdf("letter.pdf", "Dear Asha,", "Thank you for your order."));

        Assert.Contains("Thank you for your order.", docx.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void Replaces_the_office_dependent_version_in_the_registry()
    {
        var pdf = _temp.TextPdf("a.pdf", "x");
        var word = OperationRegistry.CreateDefault().For(OperationCategory.Convert, [pdf]).Single(o => o.Id == "pdf.to.docx");

        Assert.IsType<PdfToWordOperation>(word);
        Assert.Null(word.UnavailableReason);
    }

    private sealed class FakeOcr(string text) : Text.ITextRecognizer
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken) => Task.FromResult(text);
    }
}
