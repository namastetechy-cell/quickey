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
    private const int XlTypePdf = 0;
    private const int XlOpenXmlWorkbook = 51;
    private const int XlSheetHidden = 0;
    private const int PpSaveAsPdf = 32;
    private const int PpSaveAsOpenXmlPresentation = 24;
    private const int PpSaveAsPng = 18;
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    /// <param name="sheets">Decides which sheets of a multi-sheet workbook to convert (shared with the CSV and Word conversions).</param>
    public static IEnumerable<IOperation> All(SheetChooser? sheets = null)
    {
        var excelSheets = new ExcelSheetPicker(sheets ?? new SheetChooser());

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
            (app, job) => ConvertPdfInWord(app, job.Input, job.NextOutput()));

        yield return new OfficeOperation("excel.to.pdf", "PDF", Glyphs.Pdf, FileKind.Excel, OfficeApp.Excel, ".pdf",
            (app, job) => WithWorkbook(app, job.Input, book => ExportSheetsToPdf(book, excelSheets, job)));
        yield return new OfficeOperation("excel.to.xlsx", "XLSX", Glyphs.Document, FileKind.Excel, OfficeApp.Excel, ".xlsx",
            (app, job) => WithWorkbook(app, job.Input, book => book.SaveAs(job.NextOutput(), XlOpenXmlWorkbook)));

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

    private static void WithDocument(object word, string path, Action<dynamic> action)
    {
        dynamic doc = ((dynamic)word).Documents.Open(path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false);
        try { action(doc); }
        finally { doc.Close(SaveChanges: 0); }
    }

    /// <summary>
    /// Opens a PDF in Word (PDF Reflow). Word normally stops on "Word will now convert your PDF to an
    /// editable document", which a hidden instance can't show, so the conversion stalled or failed.
    /// That message is switched off for the duration and the user's own setting restored afterwards.
    /// Word also needs the document opened writable to convert it; the PDF itself is never saved over.
    /// All calls go through <see cref="Com"/>: <c>dynamic</c> can't see SaveAs2 on a PDF-converted document.
    /// </summary>
    private static void ConvertPdfInWord(object word, string path, string output)
    {
        using var warning = new PdfConvertWarningOff((string)Com.Get(word, "Version")!);
        var documents = Com.Get(word, "Documents")!;
        // Documents.Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
        var doc = Com.Call(documents, "Open", path, false, false, false) ?? Com.Get(word, "ActiveDocument")
            ?? throw new InvalidOperationException("Word couldn't open this PDF.");
        try
        {
            Com.CallFirst(doc, ["SaveAs2", "SaveAs"], output, WdFormatDocx);
        }
        finally
        {
            Com.Call(doc, "Close", 0); // wdDoNotSaveChanges: the PDF is never written to
            Marshal.FinalReleaseComObject(doc);
            Marshal.FinalReleaseComObject(documents);
        }
    }

    private sealed class PdfConvertWarningOff : IDisposable
    {
        private const string Value = "DisableConvertPdfWarning";
        private readonly Microsoft.Win32.RegistryKey? _key;
        private readonly object? _previous;

        public PdfConvertWarningOff(string officeVersion)
        {
            try
            {
                _key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{officeVersion}\Word\Options");
                _previous = _key.GetValue(Value);
                _key.SetValue(Value, 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _key = null; // try anyway; DisplayAlerts is off too
            }
        }

        public void Dispose()
        {
            if (_key is null)
                return;
            if (_previous is null)
                _key.DeleteValue(Value, throwOnMissingValue: false);
            else
                _key.SetValue(Value, _previous, Microsoft.Win32.RegistryValueKind.DWord);
            _key.Dispose();
        }
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
