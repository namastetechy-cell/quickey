using Quickey.Core.Settings;

namespace Quickey.Core.Documents;

/// <summary>Sheet decisions that carry across the workbooks of one batch.</summary>
public sealed class SheetBatch
{
    /// <summary>"Do the same for the other workbooks": reused without asking.</summary>
    public SheetChoice? ForRest { get; set; }

    /// <summary>The user cancelled; the rest of the batch is skipped.</summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// Decides which sheets of a workbook to convert: follows the saved preference, reuses the batch's
/// "same for the rest" answer, or asks. Workbooks with a single visible sheet never ask.
/// </summary>
public sealed class SheetChooser(ISheetPrompt? prompt = null, Func<AppSettings>? settings = null, Action<ExcelSheetsPolicy>? remember = null)
{
    /// <returns>The sheet names to convert, in workbook order, or null if the user cancelled.</returns>
    public async Task<IReadOnlyList<string>?> ChooseAsync(
        string file, IReadOnlyList<string> visibleSheets, string? currentSheet, int index, int count, SheetBatch batch, CancellationToken cancellationToken)
    {
        if (visibleSheets.Count <= 1)
            return visibleSheets;

        var choice = batch.ForRest ?? await DecideAsync(file, visibleSheets, currentSheet ?? visibleSheets[0], index, count, batch, cancellationToken);
        if (choice is null)
        {
            batch.Cancelled = true;
            return null;
        }
        return SheetPlanner.Resolve(visibleSheets, currentSheet, choice);
    }

    private async Task<SheetChoice?> DecideAsync(
        string file, IReadOnlyList<string> sheets, string current, int index, int count, SheetBatch batch, CancellationToken cancellationToken)
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

        var answer = await prompt.AskAsync(file, sheets, current, index, count, cancellationToken);
        if (answer is null)
            return null;
        // Picked-by-name sheets don't carry over to other workbooks; only Current/All do.
        if (answer.ApplyToRest && answer.Mode != SheetMode.Chosen)
            batch.ForRest = answer;
        if (answer.Remember && answer.Mode != SheetMode.Chosen)
            remember?.Invoke(answer.Mode == SheetMode.All ? ExcelSheetsPolicy.AllSheets : ExcelSheetsPolicy.CurrentSheet);
        return answer;
    }
}
