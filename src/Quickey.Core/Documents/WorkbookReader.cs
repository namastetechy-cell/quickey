using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelDataReader;
using ExcelNumberFormat;

namespace Quickey.Core.Documents;

/// <summary>One sheet as the text Excel would display in each cell.</summary>
public sealed record SheetData(string Name, bool Visible, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

public sealed record WorkbookData(IReadOnlyList<SheetData> Sheets, string? CurrentSheet)
{
    public IReadOnlyList<SheetData> VisibleSheets => Sheets.Where(s => s.Visible).ToList();
}

/// <summary>
/// Reads .xlsx, .xlsm, .xlsb, .xls and .csv without Excel, formatting numbers and dates with each
/// cell's own number format, the way Excel shows them. (.ods isn't supported here.)
/// </summary>
public static class WorkbookReader
{
    private static readonly string[] Supported = [".xlsx", ".xlsm", ".xlsb", ".xls", ".csv"];

    static WorkbookReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // .xls and old CSVs use code pages

    public static bool CanRead(string path) => Supported.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static WorkbookData Read(string path, CultureInfo culture)
    {
        // Share with writers so a workbook that's open in Excel can still be read.
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ExcelReaderFactory.CreateCsvReader(stream)
            : ExcelReaderFactory.CreateReader(stream);

        var sheets = new List<SheetData>();
        do
        {
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read())
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = Display(reader.GetValue(i), SafeFormat(reader, i), culture);
                rows.Add(row);
            }
            var name = string.IsNullOrEmpty(reader.Name) ? Path.GetFileNameWithoutExtension(path) : reader.Name;
            sheets.Add(new SheetData(name, reader.VisibleState is null or "visible", Trim(rows)));
        }
        while (reader.NextResult());

        return new WorkbookData(sheets, CurrentSheet(path, sheets));
    }

    /// <summary>The text Excel would show for a cell value.</summary>
    public static string Display(object? value, string? format, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case bool flag:
                return flag ? "TRUE" : "FALSE";
        }
        if (!string.IsNullOrEmpty(format) && !format.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            var numberFormat = new NumberFormat(format);
            if (numberFormat.IsValid)
            {
                try
                {
                    return numberFormat.Format(value, culture);
                }
                catch (Exception)
                {
                    // Odd custom format: fall through to the plain rendering.
                }
            }
        }
        return value switch
        {
            DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", culture) : date.ToString("yyyy-MM-dd HH:mm", culture),
            double number => number.ToString("0.##########", culture),
            IFormattable formattable => formattable.ToString(null, culture),
            _ => value.ToString() ?? "",
        };
    }

    private static string? SafeFormat(IExcelDataReader reader, int column)
    {
        try { return reader.GetNumberFormatString(column); }
        catch (Exception) { return null; } // CSV readers have no formats
    }

    /// <summary>Drops trailing empty rows and columns, which Excel files are full of.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> Trim(List<IReadOnlyList<string>> rows)
    {
        var lastRow = rows.FindLastIndex(r => r.Any(c => c.Length > 0));
        if (lastRow < 0)
            return [];
        var kept = rows.Take(lastRow + 1).ToList();
        var width = kept.Max(r => LastNonEmpty(r) + 1);
        return kept.Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? r[i] : "").ToArray()).ToList();
    }

    private static int LastNonEmpty(IReadOnlyList<string> row)
    {
        for (var i = row.Count - 1; i >= 0; i--)
            if (row[i].Length > 0)
                return i;
        return -1;
    }

    /// <summary>
    /// The sheet that was open when the workbook was last saved. Stored in .xlsx/.xlsm as
    /// workbookView/@activeTab; other formats fall back to the first visible sheet.
    /// </summary>
    private static string? CurrentSheet(string path, IReadOnlyList<SheetData> sheets)
    {
        var firstVisible = sheets.FirstOrDefault(s => s.Visible)?.Name;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".xlsx" or ".xlsm"))
            return firstVisible;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            using var workbook = zip.GetEntry("xl/workbook.xml")?.Open();
            if (workbook is null)
                return firstVisible;
            var xml = XDocument.Load(workbook);
            var activeTab = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookView")?.Attribute("activeTab")?.Value;
            var names = xml.Descendants().Where(e => e.Name.LocalName == "sheet").Select(e => e.Attribute("name")?.Value).ToList();
            if (int.TryParse(activeTab ?? "0", out var index) && index >= 0 && index < names.Count && names[index] is { } name
                && sheets.Any(s => s.Name == name && s.Visible))
                return name;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
        }
        return firstVisible;
    }
}
