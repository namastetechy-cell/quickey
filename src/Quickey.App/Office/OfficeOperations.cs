using System.IO;
using System.Runtime.InteropServices;
using Quickey.Core.Documents;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.App.Office;

/// <summary>
/// Document conversions done by the copy of Microsoft Office already on the PC, for the best
/// fidelity. Without Office these wedges show greyed out with "Needs Microsoft Word" etc.
/// </summary>
public static class OfficeOperations
{
    // Office SaveAs / export format constants.
    private const int WdExportFormatPdf = 17;
    private const int WdFormatDocx = 16;
    private const int WdFormatRtf = 6;
    private const int WdFormatUnicodeText = 7;
    private const int WdFormatFilteredHtml = 10;
    private const int XlTypePdf = 0;
    private const int XlOpenXmlWorkbook = 51;
    private const int XlCsvUtf8 = 62;
    private const int XlSheetHidden = 0;
    private const int PpSaveAsPdf = 32;
    private const int PpSaveAsOpenXmlPresentation = 24;
    private const int PpSaveAsPng = 18;
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    /// <param name="sheets">Asks which sheets of a multi-sheet workbook to convert. Without it, only the current sheet is.</param>
    /// <param name="settings">The live settings, for the "Excel workbooks with several sheets" preference.</param>
    /// <param name="rememberSheets">Saves a "don't ask again" answer as the new preference.</param>
    public static IEnumerable<IOperation> All(ISheetPrompt? sheets = null, Func<AppSettings>? settings = null, Action<ExcelSheetsPolicy>? rememberSheets = null)
    {
        var excelSheets = new ExcelSheetPicker(sheets, settings, rememberSheets);

        yield return new OfficeOperation("word.to.pdf", "PDF", Glyphs.Pdf, FileKind.Word, OfficeApp.Word, ".pdf",
            (app, job) => WithDocument(app, job.Input, doc => doc.ExportAsFixedFormat(job.NextOutput(), WdExportFormatPdf)));
        yield return new OfficeOperation("word.to.docx", "DOCX", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".docx",
            (app, job) => WithDocument(app, job.Input, doc => doc.SaveAs2(job.NextOutput(), WdFormatDocx)));
        yield return new OfficeOperation("word.to.txt", "Text", Glyphs.Text, FileKind.Word, OfficeApp.Word, ".txt",
            (app, job) => WithDocument(app, job.Input, doc => doc.SaveAs2(job.NextOutput(), WdFormatUnicodeText)));
        yield return new OfficeOperation("word.to.rtf", "RTF", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".rtf",
            (app, job) => WithDocument(app, job.Input, doc => doc.SaveAs2(job.NextOutput(), WdFormatRtf)));
        yield return new OfficeOperation("word.to.html", "HTML", Glyphs.Link, FileKind.Word, OfficeApp.Word, ".html",
            (app, job) => WithDocument(app, job.Input, doc => doc.SaveAs2(job.NextOutput(), WdFormatFilteredHtml)));

        // Word's own PDF reflow: editable, best-effort layout.
        yield return new OfficeOperation("pdf.to.docx", "Word", Glyphs.Document, FileKind.Pdf, OfficeApp.Word, ".docx",
            (app, job) => WithDocument(app, job.Input, doc => doc.SaveAs2(job.NextOutput(), WdFormatDocx)));

        yield return new OfficeOperation("excel.to.pdf", "PDF", Glyphs.Pdf, FileKind.Excel, OfficeApp.Excel, ".pdf",
            (app, job) => WithWorkbook(app, job.Input, book => ExportSheetsToPdf(book, excelSheets, job)));
        yield return new OfficeOperation("excel.to.xlsx", "XLSX", Glyphs.Document, FileKind.Excel, OfficeApp.Excel, ".xlsx",
            (app, job) => WithWorkbook(app, job.Input, book => book.SaveAs(job.NextOutput(), XlOpenXmlWorkbook)));
        yield return new OfficeOperation("excel.to.csv", "CSV", Glyphs.Text, FileKind.Excel, OfficeApp.Excel, ".csv",
            (app, job) => WithWorkbook(app, job.Input, book => SaveSheetsAsCsv(book, excelSheets, job)));

        yield return new OfficeOperation("ppt.to.pdf", "PDF", Glyphs.Pdf, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pdf",
            (app, job) => WithPresentation(app, job.Input, deck => deck.SaveAs(job.NextOutput(), PpSaveAsPdf)));
        yield return new OfficeOperation("ppt.to.pptx", "PPTX", Glyphs.Document, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pptx",
            (app, job) => WithPresentation(app, job.Input, deck => deck.SaveAs(job.NextOutput(), PpSaveAsOpenXmlPresentation)));
        // PowerPoint writes one PNG per slide into a folder named after the output path.
        yield return new OfficeOperation("ppt.to.png", "PNG slides", Glyphs.Photo, FileKind.PowerPoint, OfficeApp.PowerPoint, "",
            (app, job) => WithPresentation(app, job.Input, deck => deck.SaveAs(job.NextOutput(), PpSaveAsPng)));
    }

    /// <summary>
    /// One PDF with the chosen sheets. A single sheet is exported directly; several are exported by
    /// hiding the others in this read-only copy (hidden sheets aren't printed). If the workbook's
    /// structure is protected and sheets can't be hidden, each chosen sheet gets its own PDF instead.
    /// </summary>
    private static void ExportSheetsToPdf(dynamic book, ExcelSheetPicker picker, OfficeJob job)
    {
        var (visible, chosen) = picker.Pick((object)book, job);
        if (chosen.Count == 0)
            return; // skipped or cancelled
        if (chosen.Count == visible.Count)
        {
            book.ExportAsFixedFormat(XlTypePdf, job.NextOutput());
            return;
        }
        if (chosen.Count == 1)
        {
            chosen[0].Sheet.ExportAsFixedFormat(XlTypePdf, job.NextOutput());
            return;
        }
        try
        {
            foreach (var sheet in visible.Where(s => !chosen.Contains(s)))
                sheet.Sheet.Visible = XlSheetHidden;
            book.ExportAsFixedFormat(XlTypePdf, job.NextOutput());
        }
        catch (COMException)
        {
            foreach (var sheet in chosen)
                sheet.Sheet.ExportAsFixedFormat(XlTypePdf, job.NextOutput("-" + SheetPlanner.FileSafe(sheet.Name)));
        }
    }

    /// <summary>CSV holds one sheet, so each chosen sheet becomes its own file ("Budget-January.csv" when there are several).</summary>
    private static void SaveSheetsAsCsv(dynamic book, ExcelSheetPicker picker, OfficeJob job)
    {
        var (_, chosen) = picker.Pick((object)book, job);
        foreach (var sheet in chosen)
        {
            sheet.Sheet.Activate(); // Excel saves the active sheet as CSV
            book.SaveAs(job.NextOutput(chosen.Count > 1 ? "-" + SheetPlanner.FileSafe(sheet.Name) : null), XlCsvUtf8);
        }
    }

    private static void WithDocument(object word, string path, Action<dynamic> action)
    {
        dynamic doc = ((dynamic)word).Documents.Open(path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false);
        try { action(doc); }
        finally { doc.Close(SaveChanges: 0); }
    }

    private static void WithWorkbook(object excel, string path, Action<dynamic> action)
    {
        dynamic book = ((dynamic)excel).Workbooks.Open(path, UpdateLinks: 0, ReadOnly: true, AddToMru: false);
        try { action(book); }
        finally { book.Close(SaveChanges: false); }
    }

    private static void WithPresentation(object powerPoint, string path, Action<dynamic> action)
    {
        dynamic deck = ((dynamic)powerPoint).Presentations.Open(path, ReadOnly: MsoTrue, Untitled: MsoFalse, WithWindow: MsoFalse);
        try { action(deck); }
        finally { deck.Close(); }
    }

    /// <summary>One file in a batch: where it is, where its outputs go, and what the user decided for it.</summary>
    internal sealed class OfficeJob(string input, int index, int count, string extension, OperationContext context, OfficeBatch batch, CancellationToken cancellationToken)
    {
        public string Input { get; } = input;
        public int Index { get; } = index;
        public int Count { get; } = count;
        public OfficeBatch Batch { get; } = batch;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public List<string> Outputs { get; } = [];
        public bool Skipped { get; set; }

        /// <summary>Picks a fresh output path (never overwriting) and records it as an output.</summary>
        public string NextOutput(string? suffix = null)
        {
            var path = context.OutputPathFor(Input, extension, suffix);
            Outputs.Add(path);
            return path;
        }
    }

    /// <summary>Decisions that carry across the files of one batch.</summary>
    internal sealed class OfficeBatch
    {
        public SheetChoice? SheetsForRest { get; set; }
        public bool Cancelled { get; set; }
    }

    private sealed class OfficeOperation(
        string id, string label, string glyph, FileKind input, OfficeApp app, string extension,
        Action<object, OfficeJob> convert) : IOperation
    {
        public string Id => id;
        public string Label => label;
        public string Glyph => glyph;
        public OperationCategory Category => OperationCategory.Convert;
        public bool CountsTowardLimit => true;
        public string? UnavailableReason => app.UnavailableReason;

        public bool AppliesTo(IReadOnlyList<string> files) =>
            FileKinds.All(files, input) && (extension == "" || !files.All(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

        public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
        {
            var result = new OperationResult(Label);
            var batch = new OfficeBatch();
            try
            {
                // One Office instance for the whole batch; starting Word per file is slow.
                await app.Use(office =>
                {
                    for (var i = 0; i < files.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (batch.Cancelled)
                        {
                            result.Skipped += files.Count - i;
                            break;
                        }
                        var job = new OfficeJob(Path.GetFullPath(files[i]), i, files.Count, extension, context, batch, cancellationToken);
                        try
                        {
                            convert(office, job);
                            if (job.Skipped)
                                result.Skipped++;
                            result.Outputs.AddRange(job.Outputs.Where(o => File.Exists(o) || Directory.Exists(o)));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            result.Errors.Add(new OperationError(job.Input, ex.Message));
                        }
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Office itself failed to start.
                result.Errors.Add(new OperationError(files[0], ex.Message));
            }
            return result;
        }
    }
}
