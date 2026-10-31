using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Quickey.Core.Images;
using Quickey.Core.Operations;
using Quickey.Core.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Quickey.Core.Pdf;

public static class PdfOperations
{
    public static IEnumerable<IOperation> All(ITextRecognizer? ocr = null)
    {
        yield return new PdfToImagesOperation(ImageTarget.Png);
        yield return new PdfToImagesOperation(ImageTarget.Jpg);
        yield return new PdfToTextOperation(ocr);
        yield return new Documents.PdfToWordOperation(ocr);
        yield return new MergePdfOperation();
        yield return new SplitPdfOperation();
        yield return new RotatePdfOperation();
        yield return new OptimizePdfOperation();
    }

    internal static PdfDocument OpenForImport(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This PDF is password-protected.", ex);
        }
    }
}

/// <remarks>PDFium ships native builds for Windows, Linux and macOS only, which is all Quickey targets.</remarks>
/// <summary>Renders every page to an image: "report.png" for one page, "report-1.png", "report-2.png"... otherwise.</summary>
public sealed class PdfToImagesOperation(ImageTarget target) : PerFileOperation
{
    public const int Dpi = 150;

    public override string Id => "pdf.to." + target.Extension.TrimStart('.');
    public override string Label => target.Label;
    public override string Glyph => Glyphs.Photo;
    public override OperationCategory Category => OperationCategory.Convert;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(file);
        var pageCount = PDFtoImage.Conversion.GetPageCount(bytes);
        var options = new PDFtoImage.RenderOptions(Dpi: Dpi, WithAnnotations: true, BackgroundColor: SkiaSharp.SKColors.White);
        var outputs = new List<string>(pageCount);
        for (var page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = pageCount == 1 ? null : $"-{page + 1}";
            var output = context.OutputPathFor(file, target.Extension, suffix);
            if (target == ImageTarget.Jpg)
                PDFtoImage.Conversion.SaveJpeg(output, bytes, page, options: options);
            else
                PDFtoImage.Conversion.SavePng(output, bytes, page, options: options);
            outputs.Add(output);
        }
        return outputs;
    }
}

/// <summary>
/// Extracts the text layer in reading order. Pages without one (scans, photos of documents) are
/// rendered and read with OCR when available, so a scanned PDF still gives useful text.
/// </summary>
public sealed class PdfToTextOperation(ITextRecognizer? ocr = null) : PerFileOperation
{
    /// <summary>A page with fewer letters than this is treated as having no text layer.</summary>
    private const int MinLettersForTextLayer = 3;
    private const int OcrDpi = 200;

    public override string Id => "pdf.to.txt";
    public override string Label => "Text";
    public override string Glyph => Glyphs.Text;
    public override OperationCategory Category => OperationCategory.Convert;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        var pages = new List<string>();
        var bytes = File.ReadAllBytes(file);
        using (var document = UglyToad.PdfPig.PdfDocument.Open(bytes))
        {
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(page);
                }
                catch (Exception)
                {
                    // The layout analyser chokes on some unusual pages; the raw text is still useful.
                    text = page.Text;
                }
                pages.Add(text);
            }
        }

        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Count(char.IsLetterOrDigit) >= MinLettersForTextLayer || ocr is null)
                continue;
            using var png = new MemoryStream();
            PDFtoImage.Conversion.SavePng(png, bytes, i, options: new PDFtoImage.RenderOptions(Dpi: OcrDpi, BackgroundColor: SkiaSharp.SKColors.White));
            pages[i] = ocr.RecognizeAsync(png.ToArray(), cancellationToken).GetAwaiter().GetResult();
        }

        if (pages.All(p => p.Count(char.IsLetterOrDigit) < MinLettersForTextLayer))
            throw new InvalidOperationException(ocr is null
                ? "No text found. This looks like a scanned PDF, and text recognition isn't available here."
                : "No text found, even with text recognition. The pages may be blank or unreadable.");

        var output = context.OutputPathFor(file, ".txt");
        using var writer = new StreamWriter(output, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages.Count > 1)
                writer.WriteLine($"--- Page {i + 1} ---");
            writer.WriteLine(pages[i].Trim());
            writer.WriteLine();
        }
        return [output];
    }
}

/// <summary>Joins every dropped PDF into one, in drop order.</summary>
public sealed class MergePdfOperation : IOperation
{
    public string Id => "pdf.merge";
    public string Label => "Merge";
    public string Glyph => Glyphs.Merge;
    public OperationCategory Category => OperationCategory.Tools;
    public bool CountsTowardLimit => true;

    public bool AppliesTo(IReadOnlyList<string> files) => files.Count > 1 && FileKinds.All(files, FileKind.Pdf);

    public Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var result = new OperationResult(Label);
            using var merged = new PdfDocument();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var source = PdfOperations.OpenForImport(file);
                    foreach (var page in source.Pages)
                        merged.AddPage(page);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add(new OperationError(file, ex.Message));
                }
            }
            if (merged.PageCount > 0)
            {
                var output = context.OutputPathFor(files[0], ".pdf", "-merged");
                merged.Save(output);
                result.Outputs.Add(output);
            }
            return result;
        }, cancellationToken);
}

/// <summary>Writes each page to its own PDF: "report-1.pdf", "report-2.pdf"...</summary>
public sealed class SplitPdfOperation : PerFileOperation
{
    public override string Id => "pdf.split";
    public override string Label => "Split";
    public override string Glyph => Glyphs.Split;
    public override OperationCategory Category => OperationCategory.Tools;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        using var source = PdfOperations.OpenForImport(file);
        if (source.PageCount < 2)
            throw new InvalidOperationException("Only one page; nothing to split.");

        var outputs = new List<string>(source.PageCount);
        for (var i = 0; i < source.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var single = new PdfDocument();
            single.AddPage(source.Pages[i]);
            var output = context.OutputPathFor(file, ".pdf", $"-{i + 1}");
            single.Save(output);
            outputs.Add(output);
        }
        return outputs;
    }
}

/// <summary>Rotates every page 90° clockwise.</summary>
public sealed class RotatePdfOperation : PerFileOperation
{
    public override string Id => "pdf.rotate";
    public override string Label => "Rotate";
    public override string Glyph => Glyphs.Rotate;
    public override OperationCategory Category => OperationCategory.Tools;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        using var source = PdfOperations.OpenForImport(file);
        using var rotated = new PdfDocument();
        foreach (var page in source.Pages)
        {
            var copy = rotated.AddPage(page);
            copy.Rotate = (copy.Rotate + 90) % 360;
        }
        var output = context.OutputPathFor(file, ".pdf", "-rotated");
        rotated.Save(output);
        return [output];
    }
}

/// <summary>Rewrites the PDF with maximum stream compression and without unused objects. Lossless.</summary>
public sealed class OptimizePdfOperation : PerFileOperation
{
    public override string Id => "pdf.optimize";
    public override string Label => "Compress";
    public override string Glyph => Glyphs.Compress;
    public override OperationCategory Category => OperationCategory.Tools;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        using var source = PdfOperations.OpenForImport(file);
        using var optimized = new PdfDocument();
        optimized.Options.CompressContentStreams = true;
        optimized.Options.NoCompression = false;
        optimized.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        optimized.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
        foreach (var page in source.Pages)
            optimized.AddPage(page);

        var output = context.OutputPathFor(file, ".pdf", "-compressed");
        optimized.Save(output);
        if (new FileInfo(output).Length >= new FileInfo(file).Length)
        {
            File.Delete(output);
            throw new InvalidOperationException("Already as small as it gets.");
        }
        return [output];
    }
}
