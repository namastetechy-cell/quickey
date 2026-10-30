using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Quickey.Core.Documents;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.Core.Tests;

public class SpreadsheetTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public SpreadsheetTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        _temp.Dispose();
    }

    /// <summary>Summary, January (saved as the open sheet) and a hidden sheet, with formatted values.</summary>
    private string Budget(string name = "Budget.xlsx", int extraColumns = 0)
    {
        var path = _temp.File(name);
        using var book = new XLWorkbook();
        var summary = book.AddWorksheet("Summary");
        summary.Cell("A1").Value = "Total";
        summary.Cell("B1").Value = 1234.5;
        summary.Cell("B1").Style.NumberFormat.Format = "#,##0.00";

        var january = book.AddWorksheet("January");
        january.Cell("A1").Value = "Item";
        january.Cell("B1").Value = "Note";
        january.Cell("C1").Value = "Date";
        january.Cell("D1").Value = "Share";
        january.Cell("A2").Value = "Rent";
        january.Cell("B2").Value = "Flat 4, \"blue\" door";
        january.Cell("C2").Value = new DateTime(2026, 1, 5);
        january.Cell("C2").Style.NumberFormat.Format = "yyyy-mm-dd";
        january.Cell("D2").Value = 0.25;
        january.Cell("D2").Style.NumberFormat.Format = "0%";
        for (var i = 0; i < extraColumns; i++)
            january.Cell(1, 5 + i).Value = $"Extra {i}";
        january.SetTabActive();

        book.AddWorksheet("Secret").Hide();
        book.SaveAs(path);
        return path;
    }

    private static OperationRegistry Registry(SheetChooser? chooser = null) => OperationRegistry.CreateDefault(sheets: chooser);

    private Task<OperationResult> Run(string id, SheetChooser? chooser, params string[] files) =>
        Registry(chooser).Find(id)!.RunAsync(files, new OperationContext(), CancellationToken.None);

    [Fact]
    public void Reads_sheets_visibility_current_sheet_and_formatted_values()
    {
        var workbook = WorkbookReader.Read(Budget(), CultureInfo.InvariantCulture);

        Assert.Equal(["Summary", "January", "Secret"], workbook.Sheets.Select(s => s.Name));
        Assert.Equal(["Summary", "January"], workbook.VisibleSheets.Select(s => s.Name));
        Assert.Equal("January", workbook.CurrentSheet);
        Assert.Equal(["Total", "1,234.50"], workbook.Sheets[0].Rows[0]);
        Assert.Equal(["Rent", "Flat 4, \"blue\" door", "2026-01-05", "25%"], workbook.Sheets[1].Rows[1]);
    }

    [Fact]
    public async Task Csv_defaults_to_the_current_sheet_with_proper_quoting()
    {
        var result = await Run("excel.to.csv", null, Budget());

        var csv = Assert.Single(result.Outputs);
        Assert.Equal(_temp.File("Budget.csv"), csv);
        Assert.Equal("Item,Note,Date,Share\r\nRent,\"Flat 4, \"\"blue\"\" door\",2026-01-05,25%\r\n", File.ReadAllText(csv));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(csv)[..3]); // BOM, so Excel reads UTF-8
    }

    [Fact]
    public async Task All_sheets_preference_writes_one_csv_per_visible_sheet()
    {
        var settings = new AppSettings { ExcelSheets = ExcelSheetsPolicy.AllSheets };

        var result = await Run("excel.to.csv", new SheetChooser(settings: () => settings), Budget());

        Assert.Equal([_temp.File("Budget-Summary.csv"), _temp.File("Budget-January.csv")], result.Outputs);
    }

    [Fact]
    public async Task Word_gets_a_table_per_chosen_sheet()
    {
        var prompt = new ScriptedSheets(new SheetChoice(SheetMode.All));

        var result = await Run("excel.to.docx", new SheetChooser(prompt), Budget());

        var docx = Assert.Single(result.Outputs);
        Assert.Equal(_temp.File("Budget.docx"), docx);
        using var document = WordprocessingDocument.Open(docx, false);
        var body = document.MainDocumentPart!.Document!.Body!;
        var tables = body.Elements<Table>().ToList();
        Assert.Equal(2, tables.Count);
        Assert.Equal("1,234.50", tables[0].Descendants<TableCell>().ElementAt(1).InnerText);
        Assert.Contains("Flat 4, \"blue\" door", tables[1].InnerText);
        Assert.NotNull(tables[1].Elements<TableRow>().First().TableRowProperties?.GetFirstChild<TableHeader>());
        Assert.Contains("January", body.InnerText); // sheet headings
        Assert.Equal(1, prompt.Asked);
    }

    [Fact]
    public async Task Wide_sheets_are_laid_out_landscape()
    {
        var result = await Run("excel.to.docx", null, Budget(extraColumns: 6));

        using var document = WordprocessingDocument.Open(Assert.Single(result.Outputs), false);
        var page = document.MainDocumentPart!.Document!.Body!.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.Equal(PageOrientationValues.Landscape, page.Orient!.Value);
    }

    [Fact]
    public async Task Csv_files_convert_to_word_too()
    {
        var csv = _temp.File("list.csv");
        File.WriteAllText(csv, "Name,City\nAsha,Pune\n");

        var result = await Run("excel.to.docx", null, csv);

        using var document = WordprocessingDocument.Open(Assert.Single(result.Outputs), false);
        Assert.Contains("Pune", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public async Task Same_for_the_rest_asks_once_and_cancel_skips_the_rest()
    {
        var files = new[] { Budget("a.xlsx"), Budget("b.xlsx"), Budget("c.xlsx") };
        var once = new ScriptedSheets(new SheetChoice(SheetMode.Current, ApplyToRest: true));
        var result = await Run("excel.to.csv", new SheetChooser(once), files);
        Assert.Equal(3, result.Outputs.Count);
        Assert.Equal(1, once.Asked);

        var cancel = new ScriptedSheets(null);
        var cancelled = await Run("excel.to.csv", new SheetChooser(cancel), files);
        Assert.Empty(cancelled.Outputs);
        Assert.Equal(3, cancelled.Skipped);
        Assert.True(cancelled.Succeeded);
    }

    [Fact]
    public async Task Dont_ask_again_saves_the_preference()
    {
        ExcelSheetsPolicy? saved = null;
        var prompt = new ScriptedSheets(new SheetChoice(SheetMode.All, Remember: true));

        await Run("excel.to.csv", new SheetChooser(prompt, remember: p => saved = p), Budget());

        Assert.Equal(ExcelSheetsPolicy.AllSheets, saved);
    }

    [Fact]
    public async Task Single_sheet_workbooks_never_ask()
    {
        var path = _temp.File("one.xlsx");
        using (var book = new XLWorkbook())
        {
            book.AddWorksheet("Only").Cell("A1").Value = "x";
            book.SaveAs(path);
        }
        var prompt = new ScriptedSheets(null);

        var result = await Run("excel.to.csv", new SheetChooser(prompt), path);

        Assert.Single(result.Outputs);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public async Task Unreadable_workbooks_report_an_error()
    {
        var bad = _temp.File("broken.xlsx");
        File.WriteAllText(bad, "not a workbook");

        var result = await Run("excel.to.csv", null, bad);

        Assert.Empty(result.Outputs);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded", "\" padded\"")]
    public void Csv_quoting_follows_rfc_4180(string field, string expected) =>
        Assert.Equal(expected, SpreadsheetToCsvOperation.Quote(field));

    [Fact]
    public void Ods_is_left_to_office()
    {
        var ods = _temp.File("sheet.ods");
        File.WriteAllText(ods, "");

        Assert.DoesNotContain(Registry().For(OperationCategory.Convert, [ods]), o => o.Id is "excel.to.csv" or "excel.to.docx");
    }

    private sealed class ScriptedSheets(SheetChoice? answer) : ISheetPrompt
    {
        public int Asked { get; private set; }

        public Task<SheetChoice?> AskAsync(string file, IReadOnlyList<string> sheets, string currentSheet, int index, int count, CancellationToken cancellationToken)
        {
            Asked++;
            Assert.Equal("January", currentSheet);
            Assert.DoesNotContain("Secret", sheets); // hidden sheets aren't offered
            return Task.FromResult(answer);
        }
    }
}
