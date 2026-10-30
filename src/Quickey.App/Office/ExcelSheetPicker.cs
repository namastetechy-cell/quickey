using Quickey.Core.Documents;
using Quickey.Core.Settings;

namespace Quickey.App.Office;

/// <summary>
/// Decides which sheets of a workbook to convert: asks (default), or follows the saved
/// preference, or reuses "the same for the rest" from earlier in the batch. Workbooks with one
/// visible sheet never ask.
/// </summary>
internal sealed class ExcelSheetPicker(ISheetPrompt? prompt, Func<AppSettings>? settings, Action<ExcelSheetsPolicy>? remember)
{
    private const int XlSheetVisible = -1;

    public sealed record SheetRef(string Name, dynamic Sheet);

    /// <returns>All visible sheets, and those to convert (empty if the user skipped or cancelled).</returns>
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
        if (visible.Count <= 1)
            return (visible, visible);

        string? current = null;
        try { current = (string)book.ActiveSheet.Name; }
        catch (Exception) { /* no active sheet recorded: the planner falls back to the first */ }

        var names = visible.Select(s => s.Name).ToList();
        var choice = job.Batch.SheetsForRest ?? Ask(job, names, current ?? names[0]);
        if (choice is null)
        {
            job.Batch.Cancelled = true;
            job.Skipped = true;
            return (visible, []);
        }

        var picked = SheetPlanner.Resolve(names, current, choice);
        return (visible, visible.Where(s => picked.Contains(s.Name)).ToList());
    }

    private SheetChoice? Ask(OfficeOperations.OfficeJob job, IReadOnlyList<string> names, string current)
    {
        switch (settings?.Invoke().ExcelSheets ?? ExcelSheetsPolicy.Ask)
        {
            case ExcelSheetsPolicy.CurrentSheet:
                return new SheetChoice(SheetMode.Current);
            case ExcelSheetsPolicy.AllSheets:
                return new SheetChoice(SheetMode.All);
        }
        if (prompt is null)
            return new SheetChoice(SheetMode.Current);

        // This runs on Office's worker thread; the window lives on the UI thread, which stays free.
        var answer = prompt.AskAsync(job.Input, names, current, job.Index, job.Count, job.CancellationToken).GetAwaiter().GetResult();
        if (answer is null)
            return null;
        // Picked-by-name sheets don't carry over to other workbooks; only Current/All do.
        if (answer.ApplyToRest && answer.Mode != SheetMode.Chosen)
            job.Batch.SheetsForRest = answer;
        if (answer.Remember && answer.Mode != SheetMode.Chosen)
            remember?.Invoke(answer.Mode == SheetMode.All ? ExcelSheetsPolicy.AllSheets : ExcelSheetsPolicy.CurrentSheet);
        return answer;
    }
}
