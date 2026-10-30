using Quickey.Core.Documents;
using Quickey.Core.Settings;

namespace Quickey.Core.Tests;

public class SheetSelectionTests : IDisposable
{
    private static readonly string[] Sheets = ["Summary", "January", "February"];
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Current_means_only_the_sheet_open_when_saved() =>
        Assert.Equal(["January"], SheetPlanner.Resolve(Sheets, "January", new SheetChoice(SheetMode.Current)));

    [Fact]
    public void Unknown_current_sheet_falls_back_to_the_first() =>
        Assert.Equal(["Summary"], SheetPlanner.Resolve(Sheets, "Deleted", new SheetChoice(SheetMode.Current)));

    [Fact]
    public void All_keeps_workbook_order() =>
        Assert.Equal(Sheets, SheetPlanner.Resolve(Sheets, "January", new SheetChoice(SheetMode.All)));

    [Fact]
    public void Chosen_keeps_workbook_order_and_ignores_unknown_names() =>
        Assert.Equal(["Summary", "February"], SheetPlanner.Resolve(Sheets, "January", new SheetChoice(SheetMode.Chosen, ["February", "Nope", "Summary"])));

    [Fact]
    public void Choosing_nothing_falls_back_to_the_current_sheet() =>
        Assert.Equal(["January"], SheetPlanner.Resolve(Sheets, "January", new SheetChoice(SheetMode.Chosen, ["Nope"])));

    [Theory]
    [InlineData("Q1/Q2 Sales", "Q1-Q2 Sales")]
    [InlineData("Plan: 2025?", "Plan- 2025-")]
    [InlineData("...", "Sheet")]
    public void Sheet_names_become_safe_file_names(string sheet, string expected) =>
        Assert.Equal(expected, SheetPlanner.FileSafe(sheet));

    [Fact]
    public void Excel_policy_defaults_to_ask_and_round_trips()
    {
        Assert.Equal(ExcelSheetsPolicy.Ask, new AppSettings().ExcelSheets);
        var store = new SettingsStore(_temp.File("settings.json"));
        store.Save(new AppSettings { ExcelSheets = ExcelSheetsPolicy.CurrentSheet });
        Assert.Equal(ExcelSheetsPolicy.CurrentSheet, store.Load().ExcelSheets);
    }
}
