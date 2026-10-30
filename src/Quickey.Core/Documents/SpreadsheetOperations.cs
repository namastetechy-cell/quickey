using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Quickey.Core.Operations;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Quickey.Core.Documents;

/// <summary>Spreadsheet conversions that need no Excel: they read the workbook file directly.</summary>
public static class SpreadsheetOperations
{
    public static IEnumerable<IOperation> All(SheetChooser? sheets = null)
    {
        sheets ??= new SheetChooser();
        yield return new SpreadsheetToCsvOperation(sheets);
        yield return new SpreadsheetToWordOperation(sheets);
    }
}

/// <summary>Reads each workbook, asks which sheets, and writes the outputs. Shared by CSV and Word.</summary>
public abstract class SpreadsheetOperation(SheetChooser sheets) : IOperation
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string Glyph { get; }
    public OperationCategory Category => OperationCategory.Convert;
    public bool CountsTowardLimit => true;

    public virtual bool AppliesTo(IReadOnlyList<string> files) =>
        FileKinds.All(files, FileKind.Excel) && files.All(WorkbookReader.CanRead);

    /// <summary>Writes the chosen sheets of one workbook and returns the files written.</summary>
    protected abstract IEnumerable<string> Write(string file, IReadOnlyList<SheetData> sheets, OperationContext context);

    /// <summary>Culture used to display numbers and dates; the user's own, as Excel does.</summary>
    protected virtual CultureInfo Culture => CultureInfo.CurrentCulture;

    public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
    {
        var result = new OperationResult(Label);
        var batch = new SheetBatch();
        for (var i = 0; i < files.Count; i++)
        {
            if (batch.Cancelled)
            {
                result.Skipped += files.Count - i;
                break;
            }
            var file = files[i];
            try
            {
                var workbook = await Task.Run(() => WorkbookReader.Read(file, Culture), cancellationToken);
                var visible = workbook.VisibleSheets;
                if (visible.Count == 0)
                    throw new InvalidOperationException("The workbook has no visible sheets.");

                var names = await sheets.ChooseAsync(file, visible.Select(s => s.Name).ToList(), workbook.CurrentSheet, i, files.Count, batch, cancellationToken);
                if (names is null)
                {
                    result.Skipped += files.Count - i;
                    break;
                }
                var chosen = visible.Where(s => names.Contains(s.Name)).ToList();
                result.Outputs.AddRange(await Task.Run(() => Write(file, chosen, context).ToList(), cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add(new OperationError(file, ex is ExcelDataReader.Exceptions.ExcelReaderException ? "Couldn't read this workbook: " + ex.Message : ex.Message));
            }
        }
        return result;
    }
}

/// <summary>
/// One CSV per chosen sheet ("Budget.csv", or "Budget-January.csv" when there are several), UTF-8
/// with a byte-order mark so Excel reopens accented text correctly, and RFC 4180 quoting.
/// </summary>
public sealed class SpreadsheetToCsvOperation(SheetChooser sheets) : SpreadsheetOperation(sheets)
{
    public override string Id => "excel.to.csv";
    public override string Label => "CSV";
    public override string Glyph => Glyphs.Text;

    public override bool AppliesTo(IReadOnlyList<string> files) =>
        base.AppliesTo(files) && !files.All(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

    protected override IEnumerable<string> Write(string file, IReadOnlyList<SheetData> sheets, OperationContext context)
    {
        foreach (var sheet in sheets)
        {
            var output = context.OutputPathFor(file, ".csv", sheets.Count > 1 ? "-" + SheetPlanner.FileSafe(sheet.Name) : null);
            using var writer = new StreamWriter(output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            foreach (var row in sheet.Rows)
                writer.Write(string.Join(",", row.Select(Quote)) + "\r\n");
            yield return output;
        }
    }

    public static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 || field.StartsWith(' ') || field.EndsWith(' ')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}

/// <summary>
/// A Word document with one table per chosen sheet: header row bold and repeated on every page,
/// light grid, landscape when the sheet is wide. Editable in Word like any other table.
/// </summary>
public sealed class SpreadsheetToWordOperation(SheetChooser sheets) : SpreadsheetOperation(sheets)
{
    private const int LandscapeFromColumns = 7;

    public override string Id => "excel.to.docx";
    public override string Label => "Word";
    public override string Glyph => Glyphs.Document;

    protected override IEnumerable<string> Write(string file, IReadOnlyList<SheetData> sheets, OperationContext context)
    {
        var output = context.OutputPathFor(file, ".docx");
        using (var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            document.AddMainDocumentPart().Document = new Document(body);
            var widest = sheets.Max(s => s.ColumnCount);
            var fontHalfPoints = widest > 12 ? "14" : widest > 8 ? "16" : "20";

            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                if (i > 0)
                    body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                if (sheets.Count > 1)
                    body.Append(Heading(sheet.Name));
                if (sheet.Rows.Count == 0)
                    body.Append(new Paragraph(new Run(new WordText("This sheet is empty."))));
                else
                    body.Append(Table(sheet, fontHalfPoints), new Paragraph());
            }

            var landscape = widest >= LandscapeFromColumns;
            body.Append(new SectionProperties(
                new PageSize { Width = landscape ? 16838U : 11906U, Height = landscape ? 11906U : 16838U, Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
                new PageMargin { Top = 720, Bottom = 720, Left = 720U, Right = 720U, Header = 360U, Footer = 360U, Gutter = 0U }));
        }
        yield return output;
    }

    private static Paragraph Heading(string text) =>
        new(new ParagraphProperties(new SpacingBetweenLines { After = "120" }),
            new Run(new RunProperties(new Bold(), new FontSize { Val = "28" }), new WordText(Clean(text))));

    private static Table Table(SheetData sheet, string fontHalfPoints)
    {
        static T Line<T>() where T : BorderType, new() =>
            new() { Val = BorderValues.Single, Size = 4U, Color = "BFBFBF" };

        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableLayout { Type = TableLayoutValues.Autofit },
            new TableBorders(Line<TopBorder>(), Line<BottomBorder>(), Line<LeftBorder>(), Line<RightBorder>(), Line<InsideHorizontalBorder>(), Line<InsideVerticalBorder>())));

        var columns = sheet.ColumnCount;
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var header = r == 0;
            var row = new TableRow();
            if (header)
                row.Append(new TableRowProperties(new TableHeader()));
            for (var c = 0; c < columns; c++)
            {
                var text = c < sheet.Rows[r].Count ? sheet.Rows[r][c] : "";
                var cell = new TableCell();
                if (header)
                    cell.Append(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "E8E8F4" }));
                cell.Append(CellParagraph(text, header, fontHalfPoints));
                row.Append(cell);
            }
            table.Append(row);
        }
        return table;
    }

    private static Paragraph CellParagraph(string text, bool bold, string fontHalfPoints)
    {
        var properties = new RunProperties(new FontSize { Val = fontHalfPoints });
        if (bold)
            properties.PrependChild(new Bold());
        var run = new Run(properties);
        var lines = Clean(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                run.Append(new Break());
            run.Append(new WordText(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0", Before = "0" }), run);
    }

    /// <summary>Word XML can't hold most control characters; keep tabs and line breaks.</summary>
    private static string Clean(string text) =>
        new(text.Replace("\r\n", "\n").Replace('\r', '\n').Where(ch => ch >= ' ' || ch is '\n' or '\t').ToArray());
}
