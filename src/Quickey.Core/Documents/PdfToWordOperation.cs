using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Quickey.Core.Operations;
using Quickey.Core.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Quickey.Core.Documents;

/// <summary>
/// PDF → editable Word document, without Word. Text is grouped into lines and paragraphs top to bottom,
/// keeping headings, bold, italic and font sizes; images are placed where they appear; wide gaps
/// in a line split it into columns: runs of such lines become real Word tables, a lone label/value
/// line keeps tabs with matching tab stops. Pages without a text layer (scans) are read
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
            WordDefaults.Apply(main);
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

        /// <summary>The line split at its column breaks: each cell is a run of words.</summary>
        public IReadOnlyList<IReadOnlyList<Word>> Cells()
        {
            var cells = new List<IReadOnlyList<Word>>();
            var current = new List<Word>();
            for (var i = 0; i < Words.Count; i++)
            {
                if (IsColumnBreakBefore(i))
                {
                    cells.Add(current);
                    current = [];
                }
                current.Add(Words[i]);
            }
            cells.Add(current);
            return cells;
        }
    }

    /// <summary>A horizontal or vertical stroke on the page (a table border).</summary>
    internal readonly record struct Segment(bool Vertical, double At, double From, double To);

    /// <summary>
    /// A table drawn with grid lines, as Excel and most reports do. Its rows and columns come from
    /// the lines themselves, so touching cells ("name" right against "age") and empty cells are exact.
    /// </summary>
    internal sealed record RuledTable(IReadOnlyList<double> Columns, IReadOnlyList<double> Rows)
    {
        /// <summary>Column edges, left to right.</summary>
        public IReadOnlyList<double> Columns { get; } = Columns;

        /// <summary>Row edges, top to bottom (PDF y decreases downwards).</summary>
        public IReadOnlyList<double> Rows { get; } = Rows;

        public double Top => Rows[0];

        public bool Contains(Word word)
        {
            var (x, y) = Center(word);
            return x >= Columns[0] && x <= Columns[^1] && y <= Rows[0] && y >= Rows[^1];
        }

        public (int Row, int Column)? CellOf(Word word)
        {
            var (x, y) = Center(word);
            for (var r = 0; r < Rows.Count - 1; r++)
                if (y <= Rows[r] && y >= Rows[r + 1])
                    for (var c = 0; c < Columns.Count - 1; c++)
                        if (x >= Columns[c] && x <= Columns[c + 1])
                            return (r, c);
            return null;
        }

        /// <summary>
        /// Splits a word that straddles a column line into one word per cell. PDFs often have no
        /// space between touching cells, so "name" and "age" arrive as the single word "nameage".
        /// </summary>
        public IEnumerable<Word> SplitAtColumns(Word word)
        {
            int ColumnOf(Letter letter)
            {
                var x = (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2;
                for (var c = 0; c < Columns.Count - 1; c++)
                    if (x <= Columns[c + 1])
                        return c;
                return Columns.Count - 2;
            }

            var groups = new List<List<Letter>>();
            var previous = -1;
            foreach (var letter in word.Letters)
            {
                var column = ColumnOf(letter);
                if (column != previous || groups.Count == 0)
                    groups.Add([]);
                groups[^1].Add(letter);
                previous = column;
            }
            return groups.Count == 1 ? [word] : groups.Select(g => new Word(g));
        }

        private static (double X, double Y) Center(Word word) =>
            ((word.BoundingBox.Left + word.BoundingBox.Right) / 2, (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2);
    }

    /// <summary>Thin rectangles and axis-aligned lines in the page's drawing, as segments.</summary>
    internal static List<Segment> Segments(Page page)
    {
        const double thin = 2.5, minimum = 3;
        var segments = new List<Segment>();
        foreach (var path in page.Paths)
        foreach (var subpath in path)
        {
            if (subpath.GetBoundingRectangle() is not { } box)
                continue;
            if (box.Width <= thin && box.Height > minimum)
                segments.Add(new Segment(true, (box.Left + box.Right) / 2, box.Bottom, box.Top));
            else if (box.Height <= thin && box.Width > minimum)
                segments.Add(new Segment(false, (box.Bottom + box.Top) / 2, box.Left, box.Right));
            else if (subpath.IsDrawnAsRectangle)
            {
                // A stroked cell outline (or a shaded cell): its four edges.
                segments.Add(new Segment(true, box.Left, box.Bottom, box.Top));
                segments.Add(new Segment(true, box.Right, box.Bottom, box.Top));
                segments.Add(new Segment(false, box.Top, box.Left, box.Right));
                segments.Add(new Segment(false, box.Bottom, box.Left, box.Right));
            }
            else
            {
                foreach (var command in subpath.Commands)
                    if (command is PdfSubpath.Line line)
                    {
                        if (Math.Abs(line.From.X - line.To.X) <= 0.5 && Math.Abs(line.From.Y - line.To.Y) > minimum)
                            segments.Add(new Segment(true, line.From.X, Math.Min(line.From.Y, line.To.Y), Math.Max(line.From.Y, line.To.Y)));
                        else if (Math.Abs(line.From.Y - line.To.Y) <= 0.5 && Math.Abs(line.From.X - line.To.X) > minimum)
                            segments.Add(new Segment(false, line.From.Y, Math.Min(line.From.X, line.To.X), Math.Max(line.From.X, line.To.X)));
                    }
            }
        }
        return segments;
    }

    /// <summary>
    /// Groups touching segments into grids; a grid with at least two rows and two columns is a table.
    /// </summary>
    internal static List<RuledTable> RuledTables(IReadOnlyList<Segment> segments)
    {
        const double tolerance = 2;
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        static bool Touch(Segment a, Segment b)
        {
            if (a.Vertical == b.Vertical)
                return Math.Abs(a.At - b.At) <= tolerance && a.From <= b.To + tolerance && b.From <= a.To + tolerance;
            var (v, h) = a.Vertical ? (a, b) : (b, a);
            return v.At >= h.From - tolerance && v.At <= h.To + tolerance && h.At >= v.From - tolerance && h.At <= v.To + tolerance;
        }

        for (var i = 0; i < segments.Count; i++)
            for (var j = i + 1; j < segments.Count; j++)
                if (Touch(segments[i], segments[j]))
                    parent[Find(i)] = Find(j);

        var tables = new List<RuledTable>();
        foreach (var group in Enumerable.Range(0, segments.Count).GroupBy(Find))
        {
            var members = group.Select(i => segments[i]).ToList();
            var columns = Cluster(members.Where(s => s.Vertical).Select(s => s.At), tolerance);
            var rows = Cluster(members.Where(s => !s.Vertical).Select(s => s.At), tolerance);
            rows.Reverse();
            if (columns.Count >= 3 && rows.Count >= 3)
                tables.Add(new RuledTable(columns, rows));
        }
        return tables.OrderByDescending(t => t.Top).ToList();
    }

    /// <summary>Sorted positions with near-duplicates (within <paramref name="tolerance"/>) merged.</summary>
    private static List<double> Cluster(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (var value in values.Order())
            if (result.Count == 0 || value - result[^1] > tolerance)
                result.Add(value);
        return result;
    }

    /// <summary>
    /// Column ranges shared by a run of table rows: horizontal extents of all cells, merged where
    /// they overlap. Overlap (rather than left edges) also lines up right-aligned numbers.
    /// </summary>
    internal static List<(double Left, double Right)> Columns(IReadOnlyList<PageLine> rows)
    {
        var columns = new List<(double Left, double Right)>();
        foreach (var (left, right) in rows.SelectMany(r => r.Cells())
                     .Select(c => (Left: c[0].BoundingBox.Left, Right: c[^1].BoundingBox.Right))
                     .OrderBy(c => c.Left))
        {
            if (columns.Count > 0 && left <= columns[^1].Right + 2)
                columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, right));
            else
                columns.Add((left, right));
        }
        return columns;
    }

    /// <summary>Consecutive column-aligned lines (at least two, close together) form a table.</summary>
    private static int TableRowCount(IReadOnlyList<PageLine> lines, int start)
    {
        var end = start;
        while (end < lines.Count && lines[end].HasColumns
               && (end == start || lines[end - 1].Bottom - lines[end].Top <= lines[end - 1].Size * 2.5))
            end++;
        var count = end - start;
        return count >= 2 && Columns(lines.Skip(start).Take(count).ToList()).Count >= 2 ? count : 0;
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

    private static void WritePage(Writer writer, Page page, IReadOnlyList<Word> allWords)
    {
        var ruled = RuledTables(Segments(page));
        var tableWords = ruled.ToDictionary(t => t, t => allWords.Where(t.Contains).SelectMany(t.SplitAtColumns).ToList());
        var words = allWords.Where(w => !ruled.Any(t => t.Contains(w))).ToList();
        var nextRuled = 0;
        void RuledTablesAbove(double y)
        {
            while (nextRuled < ruled.Count && ruled[nextRuled].Top >= y)
            {
                var table = ruled[nextRuled++];
                writer.RuledTable(table, tableWords[table]);
            }
        }

        if (words.Count == 0)
        {
            RuledTablesAbove(double.MinValue);
            return;
        }
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
            if (nextRuled < ruled.Count && ruled[nextRuled].Top >= line.Top)
            {
                if (paragraph.Count > 0)
                {
                    writer.Paragraph(paragraph, bodySize, textWidth);
                    paragraph.Clear();
                }
                RuledTablesAbove(line.Top);
            }
            var tableRows = TableRowCount(lines, i);
            if (tableRows > 0)
            {
                if (paragraph.Count > 0)
                {
                    writer.Paragraph(paragraph, bodySize, textWidth);
                    paragraph.Clear();
                }
                while (nextImage < images.Count && images[nextImage].BoundingBox.Top > line.Top)
                    writer.Image(images[nextImage++], page.Width);
                writer.Table(lines.Skip(i).Take(tableRows).ToList());
                i += tableRows - 1;
                continue;
            }
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
        RuledTablesAbove(double.MinValue);
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

        /// <summary>A table from the PDF's own grid lines: one Word cell per drawn cell, same widths.</summary>
        public void RuledTable(RuledTable ruled, IReadOnlyList<Word> words)
        {
            var columnCount = ruled.Columns.Count - 1;
            var cells = new List<Word>[ruled.Rows.Count - 1, columnCount];
            foreach (var word in words)
                if (ruled.CellOf(word) is var (row, column))
                    (cells[row, column] ??= []).Add(word);

            // The drawn grid, widened where a cell's text needs more room once Word's padding is added
            // (exact PDF widths made Word wrap "name" as "nam/e").
            var widths = Enumerable.Range(0, columnCount)
                .Select(c => Math.Max(ruled.Columns[c + 1] - ruled.Columns[c],
                    Enumerable.Range(0, ruled.Rows.Count - 1).Select(r => TextWidth(cells[r, c])).DefaultIfEmpty(0).Max() + CellPadding))
                .ToList();
            var table = NewTable(widths);

            for (var r = 0; r < ruled.Rows.Count - 1; r++)
            {
                var row = new TableRow();
                for (var c = 0; c < widths.Count; c++)
                    row.Append(Cell(cells[r, c], widths[c]));
                table.Append(row);
            }
            body.Append(table, new Paragraph(Spacing()));
        }

        /// <summary>Room for Word's cell padding (both sides) and rounding, in points.</summary>
        private const double CellPadding = 9;

        /// <summary>Width of a cell's widest line of text in the PDF, in points.</summary>
        private static double TextWidth(IReadOnlyList<Word>? words) =>
            words is null || words.Count == 0 ? 0 : Lines(words).Max(l => l.Right - l.Left);

        /// <summary>A table with explicit column widths (points), tight padding and a light grid.</summary>
        private static Table NewTable(IReadOnlyList<double> widthsInPoints)
        {
            static T Line<T>() where T : BorderType, new() => new() { Val = BorderValues.Single, Size = 4U, Color = "A6A6A6" };

            var table = new Table(new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widthsInPoints.Sum() * 20)).ToString() },
                new TableBorders(Line<TopBorder>(), Line<BottomBorder>(), Line<LeftBorder>(), Line<RightBorder>(), Line<InsideHorizontalBorder>(), Line<InsideVerticalBorder>()),
                new TableLayout { Type = TableLayoutValues.Autofit },
                WordDefaults.TightCellMargins()));
            table.Append(new TableGrid(widthsInPoints.Select(w => new GridColumn { Width = ((int)(w * 20)).ToString() })));
            return table;
        }

        /// <summary>One cell with its width set (Word mis-sizes columns whose cells have none); words in reading order.</summary>
        private static TableCell Cell(IReadOnlyList<Word>? words, double widthInPoints)
        {
            var paragraph = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0", Before = "0" }));
            if (words is not null)
            {
                var lines = Lines(words);
                for (var l = 0; l < lines.Count; l++)
                {
                    if (l > 0)
                        paragraph.Append(new Run(new WordText(" ") { Space = SpaceProcessingModeValues.Preserve }));
                    for (var w = 0; w < lines[l].Words.Count; w++)
                        paragraph.Append(WordRun((w == 0 ? "" : " ") + lines[l].Words[w].Text, lines[l].Words[w], heading: false));
                }
            }
            return new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = ((int)(widthInPoints * 20)).ToString() }), paragraph);
        }

        /// <summary>A real Word table: one row per line, cells placed in the shared column ranges.</summary>
        public void Table(IReadOnlyList<PageLine> rows)
        {
            var columns = Columns(rows);
            // Text-only tables have no drawn edges: each column spans to the next one, and always fits its text.
            var widths = columns.Select((c, i) => Math.Max((i + 1 < columns.Count ? columns[i + 1].Left : c.Right + 6) - c.Left, c.Right - c.Left + CellPadding)).ToList();
            var table = NewTable(widths);

            foreach (var row in rows)
            {
                var cells = new List<Word>[columns.Count];
                foreach (var cell in row.Cells())
                {
                    var left = cell[0].BoundingBox.Left;
                    var index = columns.FindIndex(c => left >= c.Left - 2 && left <= c.Right + 2);
                    if (index < 0)
                        index = columns.Count - 1;
                    (cells[index] ??= []).AddRange(cell);
                }

                var tableRow = new TableRow();
                for (var c = 0; c < cells.Length; c++)
                    tableRow.Append(Cell(cells[c], widths[c]));
                table.Append(tableRow);
            }
            body.Append(table, new Paragraph(Spacing()));
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
