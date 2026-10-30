using Quickey.Core.Documents;

namespace Quickey.App.Office;

/// <summary>Lists a workbook's visible sheets through Excel and lets the shared <see cref="SheetChooser"/> decide which to convert.</summary>
internal sealed class ExcelSheetPicker(SheetChooser chooser)
{
    private const int XlSheetVisible = -1;

    public sealed record SheetRef(string Name, dynamic Sheet);

    /// <returns>All visible sheets, and those to convert (empty if the user cancelled).</returns>
    public (IReadOnlyList<SheetRef> Visible, IReadOnlyList<SheetRef> Chosen) Pick(object workbook, OfficeOperations.OfficeJob job)
    {
        dynamic book = workbook;
        var visible = new List<SheetRef>();
        int count = book.Sheets.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic sheet = book.Sheets.Item(i);
            if ((int)sheet.Visible == XlSheetVisible)
                visible.Add(new SheetRef((string)sheet.Name, sheet));
        }

        string? current = null;
        try { current = (string)book.ActiveSheet.Name; }
        catch (Exception) { /* no active sheet recorded: the chooser falls back to the first */ }

        // Runs on Office's worker thread; the sheet window lives on the UI thread, which stays free.
        var names = chooser.ChooseAsync(job.Input, visible.Select(s => s.Name).ToList(), current, job.Index, job.Count, job.Batch.Sheets, job.CancellationToken)
            .GetAwaiter().GetResult();
        if (names is null)
        {
            job.Skipped = true;
            return (visible, []);
        }
        return (visible, visible.Where(s => names.Contains(s.Name)).ToList());
    }
}
