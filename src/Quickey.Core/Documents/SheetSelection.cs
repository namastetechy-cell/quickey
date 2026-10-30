namespace Quickey.Core.Documents;

/// <summary>Which sheets of a workbook to convert when it has more than one.</summary>
public enum SheetMode
{
    /// <summary>The sheet that was open when the workbook was last saved.</summary>
    Current,
    All,
    Chosen,
}

/// <summary>The answer to "which sheets?" for one workbook.</summary>
/// <param name="Chosen">The sheet names picked by hand; only used with <see cref="SheetMode.Chosen"/>.</param>
/// <param name="ApplyToRest">Use the same mode for the remaining workbooks in this batch without asking.</param>
/// <param name="Remember">Make this mode the default from now on (Current or All only).</param>
public sealed record SheetChoice(SheetMode Mode, IReadOnlyList<string>? Chosen = null, bool ApplyToRest = false, bool Remember = false);

/// <summary>Asks which sheets to convert. Returns null if the user cancelled.</summary>
public interface ISheetPrompt
{
    Task<SheetChoice?> AskAsync(string file, IReadOnlyList<string> sheets, string currentSheet, int index, int count, CancellationToken cancellationToken);
}

public static class SheetPlanner
{
    /// <summary>
    /// The sheets to convert, in workbook order. Falls back to the first sheet if the saved
    /// "current" sheet is unknown, and never returns an empty list for a non-empty workbook.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> sheets, string? currentSheet, SheetChoice choice)
    {
        if (sheets.Count == 0)
            return [];
        var current = sheets.Contains(currentSheet) ? currentSheet! : sheets[0];
        return choice.Mode switch
        {
            SheetMode.All => sheets,
            SheetMode.Chosen when choice.Chosen is { Count: > 0 } picked => sheets.Where(picked.Contains).DefaultIfEmpty(current).ToList(),
            _ => [current],
        };
    }

    /// <summary>A sheet name made safe for a file name: "Q1/Q2 Sales" → "Q1-Q2 Sales".</summary>
    public static string FileSafe(string sheet)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(sheet.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "Sheet" : safe;
    }
}
