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
        var order = new[] { "Quarterly Report", "Prepared for the board", "Revenue grew", "three years", "Item", "Rent" }.Select(s => text.IndexOf(s)).ToList();
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

    private static List<List<string>> Cells(Table table) =>
        table.Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();

    [Fact]
    public async Task Rows_in_columns_become_a_real_table()
    {
        using var docx = await Convert(Report());

        var table = Assert.Single(docx.MainDocumentPart!.Document!.Body!.Elements<Table>());
        Assert.Equal([["Item", "Amount"], ["Rent", "1,200"]], Cells(table));
        Assert.NotNull(table.Descendants<Run>().First(r => r.InnerText == "Item").RunProperties!.Bold);
    }

    [Fact]
    public async Task Right_aligned_numbers_stay_in_their_column()
    {
        var path = _temp.File("gst.pdf");
        new PdfBuilder()
            .Text(72, 700, "Invoice", font: 2).Text(250, 700, "Taxable", font: 2).Text(400, 700, "GST", font: 2)
            .Text(72, 686, "INV-001").Text(262, 686, "1,200.00").Text(400, 686, "216.00")
            .Text(72, 672, "INV-002").Text(250, 672, "15,000.00").Text(394, 672, "2,700.00")
            .Text(72, 658, "Total", font: 2).Text(250, 658, "16,200.00", font: 2).Text(394, 658, "2,916.00", font: 2)
            .Save(path);

        using var docx = await Convert(path);

        var table = Assert.Single(docx.MainDocumentPart!.Document!.Body!.Elements<Table>());
        Assert.Equal(
            [["Invoice", "Taxable", "GST"], ["INV-001", "1,200.00", "216.00"], ["INV-002", "15,000.00", "2,700.00"], ["Total", "16,200.00", "2,916.00"]],
            Cells(table));
    }

    /// <summary>
    /// The user's Excel export: narrow columns where "name" and "age" nearly touch, an empty middle
    /// column, and cell borders drawn as grid lines (or thin bars).
    /// </summary>
    private string ExcelGrid(bool asBars)
    {
        var path = _temp.File(asBars ? "grid-bars.pdf" : "grid-lines.pdf");
        double[] xs = [52, 79.6, 96.8, 112];
        double[] ys = [790, 774, 758, 742];
        var pdf = new PdfBuilder()
            .Text(54, 777, "name").Text(80.4, 777, "age")
            .Text(54, 761, "r").Text(98, 761, "21")
            .Text(54, 745, "v").Text(98, 745, "21");
        foreach (var x in xs)
            if (asBars) pdf.Bar(x - 0.25, ys[^1], 0.5, ys[0] - ys[^1]); else pdf.Line(x, ys[^1], x, ys[0]);
        foreach (var y in ys)
            if (asBars) pdf.Bar(xs[0], y - 0.25, xs[^1] - xs[0], 0.5); else pdf.Line(xs[0], y, xs[^1], y);
        pdf.Save(path);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grid_lines_define_the_table_even_when_cells_touch(bool asBars)
    {
        using var docx = await Convert(ExcelGrid(asBars));

        var table = Assert.Single(docx.MainDocumentPart!.Document!.Body!.Elements<Table>());
        Assert.Equal([["name", "age", ""], ["r", "", "21"], ["v", "", "21"]], Cells(table));
    }

    [Fact]
    public async Task Every_cell_has_a_width_that_fits_its_text()
    {
        using var docx = await Convert(ExcelGrid(asBars: false));

        var table = docx.MainDocumentPart!.Document!.Body!.Elements<Table>().Single();
        Assert.All(table.Descendants<TableCell>(), c => Assert.NotNull(c.TableCellProperties?.TableCellWidth));
        // "name" is ~23pt wide in the PDF; with padding its column must be wider than that, or Word wraps it.
        var first = int.Parse(table.GetFirstChild<TableGrid>()!.Elements<GridColumn>().First().Width!.Value!);
        Assert.True(first >= 30 * 20, $"first column {first} twips");
    }

    [Fact]
    public async Task Documents_open_in_modern_word_mode_with_calibri()
    {
        using var docx = await Convert(ExcelGrid(asBars: false));

        var compat = docx.MainDocumentPart!.DocumentSettingsPart!.Settings!.Descendants<CompatibilitySetting>().Single();
        Assert.Equal("15", compat.Val!.Value);
        Assert.Equal("Calibri", docx.MainDocumentPart.StyleDefinitionsPart!.Styles!.Descendants<RunFonts>().First().Ascii!.Value);
    }

    [Fact]
    public async Task A_lone_label_and_value_line_stays_a_tabbed_paragraph()
    {
        var path = _temp.File("form.pdf");
        new PdfBuilder()
            .Text(72, 700, "Name:").Text(250, 700, "Asha Rao")
            .Text(72, 660, "Thanks for your business.")
            .Save(path);

        using var docx = await Convert(path);

        var body = docx.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Elements<Table>());
        Assert.Contains("Name:\tAsha Rao", body.InnerText);
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
