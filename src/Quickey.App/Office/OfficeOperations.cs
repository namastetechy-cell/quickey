using System.IO;
using System.Runtime.InteropServices;
using Quickey.Core.Documents;
using Quickey.Core.Operations;

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
    private const int WdDoNotSaveChanges = 0;
    private const int XlTypePdf = 0;
    private const int XlOpenXmlWorkbook = 51;
    private const int XlSheetHidden = 0;
    private const int PpSaveAsPdf = 32;
    private const int PpSaveAsOpenXmlPresentation = 24;
    private const int PpSaveAsPng = 18;
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    private static readonly object Missing = System.Reflection.Missing.Value;

    // Every Office call goes through Dispatch (IDispatch by name), never C# dynamic: on some
    // installations dynamic can't see methods such as SaveAs2 ("'System.__ComObject' does not
    // contain a definition for 'SaveAs2'"), while calling them by name works everywhere.

    /// <param name="sheets">Decides which sheets of a multi-sheet workbook to convert (shared with the CSV and Word conversions).</param>
    /// <remarks>PDF → Word isn't here: Quickey converts PDFs itself (Quickey.Core), without Word.</remarks>
    public static IEnumerable<IOperation> All(SheetChooser? sheets = null)
    {
        var excelSheets = new ExcelSheetPicker(sheets ?? new SheetChooser());

        yield return new OfficeOperation("word.to.pdf", "PDF", Glyphs.Pdf, FileKind.Word, OfficeApp.Word, ".pdf",
            (app, job) => WithDocument(app, job.Input, doc => Dispatch.Call(doc, "ExportAsFixedFormat", job.NextOutput(), WdExportFormatPdf)));
        yield return new OfficeOperation("word.to.docx", "DOCX", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".docx",
            (app, job) => WithDocument(app, job.Input, doc => SaveWordAs(doc, job.NextOutput(), WdFormatDocx)));
        yield return new OfficeOperation("word.to.txt", "Text", Glyphs.Text, FileKind.Word, OfficeApp.Word, ".txt",
            (app, job) => WithDocument(app, job.Input, doc => SaveWordAs(doc, job.NextOutput(), WdFormatUnicodeText)));
        yield return new OfficeOperation("word.to.rtf", "RTF", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".rtf",
            (app, job) => WithDocument(app, job.Input, doc => SaveWordAs(doc, job.NextOutput(), WdFormatRtf)));
        yield return new OfficeOperation("word.to.html", "HTML", Glyphs.Link, FileKind.Word, OfficeApp.Word, ".html",
            (app, job) => WithDocument(app, job.Input, doc => SaveWordAs(doc, job.NextOutput(), WdFormatFilteredHtml)));

        yield return new OfficeOperation("excel.to.pdf", "PDF", Glyphs.Pdf, FileKind.Excel, OfficeApp.Excel, ".pdf",
            (app, job) => WithWorkbook(app, job.Input, book => ExportSheetsToPdf(book, excelSheets, job)));
        yield return new OfficeOperation("excel.to.xlsx", "XLSX", Glyphs.Document, FileKind.Excel, OfficeApp.Excel, ".xlsx",
            (app, job) => WithWorkbook(app, job.Input, book => Dispatch.Call(book, "SaveAs", job.NextOutput(), XlOpenXmlWorkbook)));

        yield return new OfficeOperation("ppt.to.pdf", "PDF", Glyphs.Pdf, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pdf",
            (app, job) => WithPresentation(app, job.Input, deck => Dispatch.Call(deck, "SaveAs", job.NextOutput(), PpSaveAsPdf)));
        yield return new OfficeOperation("ppt.to.pptx", "PPTX", Glyphs.Document, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pptx",
            (app, job) => WithPresentation(app, job.Input, deck => Dispatch.Call(deck, "SaveAs", job.NextOutput(), PpSaveAsOpenXmlPresentation)));
        // PowerPoint writes one PNG per slide into a folder named after the output path.
        yield return new OfficeOperation("ppt.to.png", "PNG slides", Glyphs.Photo, FileKind.PowerPoint, OfficeApp.PowerPoint, "",
            (app, job) => WithPresentation(app, job.Input, deck => Dispatch.Call(deck, "SaveAs", job.NextOutput(), PpSaveAsPng)));
    }

    /// <summary>SaveAs2 (Word 2010+), falling back to SaveAs on older Word.</summary>
    private static void SaveWordAs(object doc, string output, int format) =>
        Dispatch.CallFirst(doc, ["SaveAs2", "SaveAs"], output, format);

    /// <summary>
    /// One PDF with the chosen sheets. A single sheet is exported directly; several are exported by
    /// hiding the others in this read-only copy (hidden sheets aren't printed). If the workbook's
    /// structure is protected and sheets can't be hidden, each chosen sheet gets its own PDF instead.
    /// </summary>
    private static void ExportSheetsToPdf(object book, ExcelSheetPicker picker, OfficeJob job)
    {
        var (visible, chosen) = picker.Pick(book, job);
        if (chosen.Count == 0)
            return; // skipped or cancelled
        if (chosen.Count == visible.Count)
        {
            Dispatch.Call(book, "ExportAsFixedFormat", XlTypePdf, job.NextOutput());
            return;
        }
        if (chosen.Count == 1)
        {
            Dispatch.Call(chosen[0].Sheet, "ExportAsFixedFormat", XlTypePdf, job.NextOutput());
            return;
        }
        try
        {
            foreach (var sheet in visible.Where(s => !chosen.Contains(s)))
                Dispatch.Set(sheet.Sheet, "Visible", XlSheetHidden);
            Dispatch.Call(book, "ExportAsFixedFormat", XlTypePdf, job.NextOutput());
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException)
        {
            foreach (var sheet in chosen)
                Dispatch.Call(sheet.Sheet, "ExportAsFixedFormat", XlTypePdf, job.NextOutput("-" + SheetPlanner.FileSafe(sheet.Name)));
        }
    }

    private static void WithDocument(object word, string path, Action<object> action)
    {
        var documents = Dispatch.Get(word, "Documents")!;
        // Documents.Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate,
        //                Revert, WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible)
        var doc = Dispatch.Call(documents, "Open", path, false, true, false, Missing, Missing, Missing, Missing, Missing, Missing, Missing, false)
            ?? throw new InvalidOperationException("Word couldn't open this document.");
        try { action(doc); }
        finally
        {
            Dispatch.Call(doc, "Close", WdDoNotSaveChanges);
            Release(doc, documents);
        }
    }

    private static void WithWorkbook(object excel, string path, Action<object> action)
    {
        var workbooks = Dispatch.Get(excel, "Workbooks")!;
        // Workbooks.Open(Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended,
        //                Origin, Delimiter, Editable, Notify, Converter, AddToMru)
        var book = Dispatch.Call(workbooks, "Open", path, 0, true, Missing, Missing, Missing, Missing, Missing, Missing, Missing, Missing, Missing, false)
            ?? throw new InvalidOperationException("Excel couldn't open this workbook.");
        try { action(book); }
        finally
        {
            Dispatch.Call(book, "Close", false);
            Release(book, workbooks);
        }
    }

    private static void WithPresentation(object powerPoint, string path, Action<object> action)
    {
        var presentations = Dispatch.Get(powerPoint, "Presentations")!;
        // Presentations.Open(FileName, ReadOnly, Untitled, WithWindow)
        var deck = Dispatch.Call(presentations, "Open", path, MsoTrue, MsoFalse, MsoFalse)
            ?? throw new InvalidOperationException("PowerPoint couldn't open this presentation.");
        try { action(deck); }
        finally
        {
            Dispatch.Call(deck, "Close");
            Release(deck, presentations);
        }
    }

    private static void Release(params object[] comObjects)
    {
        foreach (var comObject in comObjects)
            if (Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
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
        public SheetBatch Sheets { get; } = new();
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
                        if (batch.Sheets.Cancelled)
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
