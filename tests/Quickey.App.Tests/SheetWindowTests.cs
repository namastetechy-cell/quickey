using Quickey.App.UI;
using Quickey.Core.Documents;

namespace Quickey.App.Tests;

/// <summary>The "which sheets?" window (Windows only, runs in CI). Office itself isn't on the CI machines.</summary>
public class SheetWindowTests
{
    private static readonly string[] Sheets = ["Summary", "January", "February"];

    [Fact]
    public void Defaults_to_the_current_sheet()
    {
        Sta.Run(async () =>
        {
            var window = new SheetWindow(@"C:\Budget.xlsx", Sheets, "January", 0, 1);
            window.Show();

            var choice = window.Choice();
            Assert.Equal(SheetMode.Current, choice.Mode);
            Assert.Equal(["January"], SheetPlanner.Resolve(Sheets, "January", choice));

            window.Finish(choice);
            Assert.Same(choice, await window.Result);
        });
    }

    [Fact]
    public void Choosing_sheets_by_hand_never_carries_over()
    {
        Sta.Run(async () =>
        {
            var window = new SheetWindow(@"C:\Budget.xlsx", Sheets, "January", 0, 3);
            window.Show();

            window.ApplyToRest = true;
            window.Remember = true;
            window.SetMode(SheetMode.Chosen);
            window.Check("January", false);
            window.Check("Summary", true);
            window.Check("February", true);

            var choice = window.Choice();
            Assert.Equal(SheetMode.Chosen, choice.Mode);
            Assert.Equal(["Summary", "February"], choice.Chosen);
            Assert.False(choice.ApplyToRest);
            Assert.False(choice.Remember);

            window.Close();
            Assert.Null(await window.Result); // closing cancels
        });
    }

    [Fact]
    public void All_sheets_can_apply_to_the_rest_and_be_remembered()
    {
        Sta.Run(async () =>
        {
            var window = new SheetWindow(@"C:\Budget.xlsx", Sheets, "January", 0, 2);
            window.Show();
            window.SetMode(SheetMode.All);
            window.ApplyToRest = true;
            window.Remember = true;

            var choice = window.Choice();
            Assert.Equal(new SheetChoice(SheetMode.All, null, ApplyToRest: true, Remember: true), choice with { Chosen = null });
            window.Close();
            await window.Result;
        });
    }
}
