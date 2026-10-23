using System.IO;
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
    private const int XlCsvUtf8 = 62;
    private const int PpSaveAsPdf = 32;
    private const int PpSaveAsOpenXmlPresentation = 24;
    private const int PpSaveAsPng = 18;
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    public static IEnumerable<IOperation> All()
    {
        yield return new OfficeOperation("word.to.pdf", "PDF", Glyphs.Pdf, FileKind.Word, OfficeApp.Word, ".pdf",
            (app, input, output) => WithDocument(app, input, doc => doc.ExportAsFixedFormat(output, WdExportFormatPdf)));
        yield return new OfficeOperation("word.to.docx", "DOCX", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".docx",
            (app, input, output) => WithDocument(app, input, doc => doc.SaveAs2(output, WdFormatDocx)));
        yield return new OfficeOperation("word.to.txt", "Text", Glyphs.Text, FileKind.Word, OfficeApp.Word, ".txt",
            (app, input, output) => WithDocument(app, input, doc => doc.SaveAs2(output, WdFormatUnicodeText)));
        yield return new OfficeOperation("word.to.rtf", "RTF", Glyphs.Document, FileKind.Word, OfficeApp.Word, ".rtf",
            (app, input, output) => WithDocument(app, input, doc => doc.SaveAs2(output, WdFormatRtf)));
        yield return new OfficeOperation("word.to.html", "HTML", Glyphs.Link, FileKind.Word, OfficeApp.Word, ".html",
            (app, input, output) => WithDocument(app, input, doc => doc.SaveAs2(output, WdFormatFilteredHtml)));

        // Word's own PDF reflow: editable, best-effort layout.
        yield return new OfficeOperation("pdf.to.docx", "Word", Glyphs.Document, FileKind.Pdf, OfficeApp.Word, ".docx",
            (app, input, output) => WithDocument(app, input, doc => doc.SaveAs2(output, WdFormatDocx)));

        yield return new OfficeOperation("excel.to.pdf", "PDF", Glyphs.Pdf, FileKind.Excel, OfficeApp.Excel, ".pdf",
            (app, input, output) => WithWorkbook(app, input, book => book.ExportAsFixedFormat(XlTypePdf, output)));
        yield return new OfficeOperation("excel.to.xlsx", "XLSX", Glyphs.Document, FileKind.Excel, OfficeApp.Excel, ".xlsx",
            (app, input, output) => WithWorkbook(app, input, book => book.SaveAs(output, XlOpenXmlWorkbook)));
        yield return new OfficeOperation("excel.to.csv", "CSV", Glyphs.Text, FileKind.Excel, OfficeApp.Excel, ".csv",
            (app, input, output) => WithWorkbook(app, input, book => book.SaveAs(output, XlCsvUtf8)));

        yield return new OfficeOperation("ppt.to.pdf", "PDF", Glyphs.Pdf, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pdf",
            (app, input, output) => WithPresentation(app, input, deck => deck.SaveAs(output, PpSaveAsPdf)));
        yield return new OfficeOperation("ppt.to.pptx", "PPTX", Glyphs.Document, FileKind.PowerPoint, OfficeApp.PowerPoint, ".pptx",
            (app, input, output) => WithPresentation(app, input, deck => deck.SaveAs(output, PpSaveAsOpenXmlPresentation)));
        // PowerPoint writes one PNG per slide into a folder named after the output path.
        yield return new OfficeOperation("ppt.to.png", "PNG slides", Glyphs.Photo, FileKind.PowerPoint, OfficeApp.PowerPoint, "",
            (app, input, output) => WithPresentation(app, input, deck => deck.SaveAs(output, PpSaveAsPng)));
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

    private sealed class OfficeOperation(
        string id, string label, string glyph, FileKind input, OfficeApp app, string extension,
        Action<object, string, string> convert) : IOperation
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
            var jobs = files.Select(f => (Input: Path.GetFullPath(f), Output: context.OutputPathFor(f, extension))).ToList();
            try
            {
                // One Office instance for the whole batch; starting Word per file is slow.
                await app.Use(office =>
                {
                    foreach (var (inputPath, outputPath) in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            convert(office, inputPath, outputPath);
                            result.Outputs.Add(outputPath);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            result.Errors.Add(new OperationError(inputPath, ex.Message));
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
