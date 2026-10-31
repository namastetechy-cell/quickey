using Quickey.Core.Documents;

namespace Quickey.App.Office;

/// <summary>Lists a workbook's visible sheets through Excel and lets the shared <see cref="SheetChooser"/> decide which to convert.</summary>
internal sealed class ExcelSheetPicker(SheetChooser chooser)
{
    private const int XlSheetVisible = -1;

    public sealed record SheetRef(string Name, object Sheet);

    /// <returns>All visible sheets, and those to convert (empty if the user cancelled).</returns>
    public (IReadOnlyList<SheetRef> Visible, IReadOnlyList<SheetRef> Chosen) Pick(object workbook, OfficeOperations.OfficeJob job)
    {
        var visible = new List<SheetRef>();
        var sheets = Dispatch.Get(workbook, "Sheets")!;
        var count = Convert.ToInt32(Dispatch.Get(sheets, "Count"));
        for (var i = 1; i <= count; i++)
        {
            var sheet = Dispatch.Call(sheets, "Item", i)!;
            if (Convert.ToInt32(Dispatch.Get(sheet, "Visible")) == XlSheetVisible)
                visible.Add(new SheetRef((string)Dispatch.Get(sheet, "Name")!, sheet));
        }

        string? current = null;
        try { current = Dispatch.Get(Dispatch.Get(workbook, "ActiveSheet")!, "Name") as string; }
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
