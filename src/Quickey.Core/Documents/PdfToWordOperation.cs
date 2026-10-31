using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Quickey.Core.Operations;
using Quickey.Core.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Quickey.Core.Documents;

/// <summary>
/// PDF → editable Word document, without Word. Text is grouped into lines and paragraphs top to bottom,
/// keeping headings, bold, italic and font sizes; images are placed where they appear; wide gaps
/// in a line (columns of a table or form) become tabs with matching tab stops. Pages without a text layer (scans) are read
/// with OCR when available, otherwise inserted as a picture so nothing is lost. Layout is
/// approximated, not reproduced exactly: this is for editing the content.
/// </summary>
public sealed class PdfToWordOperation(ITextRecognizer? ocr = null) : PerFileOperation
{
    private const int ScanDpi = 150;
    private const long EmuPerPoint = 12700;

    public override string Id => "pdf.to.docx";
    public override string Label => "Word";
    public override string Glyph => Glyphs.Document;
    public override OperationCategory Category => OperationCategory.Convert;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Pdf);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        var bytes = File.ReadAllBytes(file);
        var output = context.OutputPathFor(file, ".docx");
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes))
        using (var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            var writer = new Writer(main, body);

            Page? first = null;
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                first ??= page;
                if (page.Number > 1)
                    body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

                var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                if (words.Count > 0)
                    WritePage(writer, page, words);
                else
                    WriteScannedPage(writer, page, bytes, cancellationToken);
            }

            var (width, height) = first is null ? (595.0, 842.0) : (first.Width, first.Height);
            body.Append(new SectionProperties(
                new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = (UInt32Value)(uint)(width * 20), Height = (UInt32Value)(uint)(height * 20), Orient = width > height ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
                new PageMargin { Top = 1080, Bottom = 1080, Left = 1080U, Right = 1080U, Header = 540U, Footer = 540U, Gutter = 0U }));
        }
        return [output];
    }

    /// <summary>One line of text across the whole page: words sharing a baseline, left to right.</summary>
    internal sealed record PageLine(IReadOnlyList<Word> Words)
    {
        public double Top => Words.Max(w => w.BoundingBox.Top);
        public double Bottom => Words.Min(w => w.BoundingBox.Bottom);
        public double Left => Words[0].BoundingBox.Left;
        public double Right => Words[^1].BoundingBox.Right;
        public double Size => Median(Words.SelectMany(w => w.Letters).Select(l => l.PointSize));

        /// <summary>Word gaps wider than ~1.5 characters are column breaks (tables, forms, label/value).</summary>
        public bool IsColumnBreakBefore(int index) =>
            index > 0 && Words[index].BoundingBox.Left - Words[index - 1].BoundingBox.Right > WordSize(Words[index]) * 1.5;

        public bool HasColumns => Enumerable.Range(1, Words.Count - 1).Any(IsColumnBreakBefore);
    }

    /// <summary>
    /// Groups words into page-wide lines by baseline. Unlike column-detecting layout analysis, this
    /// keeps a table row ("Rent    1,200") together, which matters more for business documents.
    /// </summary>
    internal static List<PageLine> Lines(IEnumerable<Word> words)
    {
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var baseline = word.BoundingBox.Bottom;
            var tolerance = WordSize(word) * 0.5;
            var line = lines.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - baseline) <= tolerance);
            if (line is null)
                lines.Add([word]);
            else
                line.Add(word);
        }
        return lines.Select(l => new PageLine(l.OrderBy(w => w.BoundingBox.Left).ToList())).ToList();
    }

    private static void WritePage(Writer writer, Page page, IReadOnlyList<Word> words)
    {
        var lines = Lines(words);
        var bodySize = Median(words.SelectMany(w => w.Letters).Select(l => l.PointSize));
        var textWidth = lines.Max(l => l.Right) - lines.Min(l => l.Left);

        // Images are interleaved with text by their vertical position (PDF y grows upwards).
        var images = page.GetImages().OrderByDescending(i => i.BoundingBox.Top).ToList();
        var nextImage = 0;
        var paragraph = new List<PageLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (paragraph.Count > 0 && !Continues(paragraph[^1], line, textWidth))
            {
                writer.Paragraph(paragraph, bodySize, textWidth);
                paragraph.Clear();
            }
            if (paragraph.Count == 0)
                while (nextImage < images.Count && images[nextImage].BoundingBox.Top > line.Top)
                    writer.Image(images[nextImage++], page.Width);
            paragraph.Add(line);
        }
        if (paragraph.Count > 0)
            writer.Paragraph(paragraph, bodySize, textWidth);
        while (nextImage < images.Count)
            writer.Image(images[nextImage++], page.Width);
    }

    /// <summary>Whether <paramref name="next"/> belongs to the same paragraph as <paramref name="previous"/>.</summary>
    private static bool Continues(PageLine previous, PageLine next, double textWidth)
    {
        var size = previous.Size;
        var gap = previous.Bottom - next.Top;
        return gap <= size * 0.9                                     // no blank line between them
            && Math.Abs(next.Size - size) <= size * 0.15             // same text size
            && Math.Abs(next.Left - previous.Left) <= size * 2       // same left edge (no new indent)
            && !previous.HasColumns && !next.HasColumns;             // table rows stay separate
    }

    private void WriteScannedPage(Writer writer, Page page, byte[] pdf, CancellationToken cancellationToken)
    {
        using var png = new MemoryStream();
        PDFtoImage.Conversion.SavePng(png, pdf, page.Number - 1, options: new PDFtoImage.RenderOptions(Dpi: ScanDpi, BackgroundColor: SkiaSharp.SKColors.White));
        if (ocr is not null)
        {
            var text = ocr.RecognizeAsync(png.ToArray(), cancellationToken).GetAwaiter().GetResult();
            if (text.Any(char.IsLetterOrDigit))
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    writer.PlainParagraph(line.TrimEnd('\r'));
                return;
            }
        }
        writer.Picture(png.ToArray(), isPng: true, page.Width, page.Height, page.Width);
    }

    private static double WordSize(Word word) => word.Letters.Count == 0 ? 11 : word.Letters.Average(l => l.PointSize);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).Order().ToList();
        return sorted.Count == 0 ? 11 : sorted[sorted.Count / 2];
    }

    /// <summary>Appends paragraphs, runs and pictures to the document body.</summary>
    private sealed class Writer(MainDocumentPart main, Body body)
    {
        private uint _pictureId;

        public void PlainParagraph(string text) =>
            body.Append(new Paragraph(Spacing(), new Run(new WordText(Clean(text)) { Space = SpaceProcessingModeValues.Preserve })));

        public void Paragraph(IReadOnlyList<PageLine> lines, double bodySize, double textWidth)
        {
            var paragraph = new Paragraph(Spacing());
            var heading = lines.Count <= 3 && Median(lines.Select(l => l.Size)) >= bodySize * 1.25;
            if (lines.Any(l => l.HasColumns))
                paragraph.ParagraphProperties!.Append(TabStops(lines));

            for (var l = 0; l < lines.Count; l++)
            {
                var line = lines[l];
                for (var w = 0; w < line.Words.Count; w++)
                {
                    var separator = w == 0 ? "" : line.IsColumnBreakBefore(w) ? "\t" : " ";
                    paragraph.Append(WordRun(separator + line.Words[w].Text, line.Words[w], heading));
                }
                if (l < lines.Count - 1)
                {
                    // Reflow wrapped lines into one paragraph; keep short lines (addresses, lists) as lines.
                    var full = line.Right - line.Left >= textWidth * 0.75;
                    var next = lines[l + 1].Words[0].Text;
                    var startsItem = next.Length > 0 && (next[0] is '•' or '-' or '–' or '*' || char.IsDigit(next[0]) && next.EndsWith('.'));
                    paragraph.Append(full && !startsItem
                        ? new Run(new WordText(" ") { Space = SpaceProcessingModeValues.Preserve })
                        : new Run(new Break()));
                }
            }
            body.Append(paragraph);
        }

        /// <summary>Tab stops at the columns' left edges, so table rows line up in Word.</summary>
        private static Tabs TabStops(IReadOnlyList<PageLine> lines)
        {
            var origin = lines.Min(l => l.Left);
            var stops = lines
                .SelectMany(l => Enumerable.Range(1, l.Words.Count - 1).Where(l.IsColumnBreakBefore).Select(i => l.Words[i].BoundingBox.Left - origin))
                .Select(points => (int)Math.Round(points * 20 / 120) * 120) // twips, snapped to 1/12 inch so rows share stops
                .Where(twips => twips > 0)
                .Distinct().Order().ToList();
            return new Tabs(stops.Select(t => new TabStop { Val = TabStopValues.Left, Position = t }));
        }

        private static Run WordRun(string text, Word word, bool heading)
        {
            var font = word.Letters.Count > 0 ? word.Letters[0].FontName ?? "" : "";
            var properties = new RunProperties();
            if (heading || font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase) || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
                properties.Append(new Bold());
            if (font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
                properties.Append(new Italic());
            var halfPoints = (int)Math.Round(Math.Clamp(WordSize(word), 5, 72) * 2);
            properties.Append(new FontSize { Val = halfPoints.ToString() });
            return new Run(properties, new WordText(Clean(text)) { Space = SpaceProcessingModeValues.Preserve });
        }


        public void Image(IPdfImage image, double pageWidth)
        {
            byte[]? data = null;
            var isPng = false;
            try
            {
                if (image.TryGetPng(out var png))
                {
                    data = png;
                    isPng = true;
                }
                else
                {
                    var raw = image.RawBytes.ToArray();
                    if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8)
                        data = raw; // JPEG as stored in the PDF
                }
            }
            catch (Exception)
            {
                // Unusual image encodings are skipped rather than failing the whole document.
            }
            if (data is not null && image.BoundingBox.Width > 1 && image.BoundingBox.Height > 1)
                Picture(data, isPng, image.BoundingBox.Width, image.BoundingBox.Height, pageWidth);
        }

        /// <summary>Inserts a picture of the given size in points, shrunk to fit the text width.</summary>
        public void Picture(byte[] data, bool isPng, double widthPoints, double heightPoints, double pageWidth)
        {
            var maxWidth = Math.Max(72, pageWidth - 108); // page minus the 0.75" margins
            if (widthPoints > maxWidth)
            {
                heightPoints *= maxWidth / widthPoints;
                widthPoints = maxWidth;
            }
            var part = main.AddImagePart(isPng ? ImagePartType.Png : ImagePartType.Jpeg);
            using (var stream = new MemoryStream(data))
                part.FeedData(stream);
            var id = ++_pictureId;
            long cx = (long)(widthPoints * EmuPerPoint), cy = (long)(heightPoints * EmuPerPoint);

            var inline = new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Picture {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image{id}" }, new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };

            body.Append(new Paragraph(Spacing(), new Run(new Drawing(inline))));
        }

        private static ParagraphProperties Spacing() => new(new SpacingBetweenLines { After = "120", Before = "0" });

        /// <summary>Word XML can't hold most control characters.</summary>
        private static string Clean(string text) => new(text.Where(ch => ch >= ' ' || ch == '\t').ToArray());
    }
}
