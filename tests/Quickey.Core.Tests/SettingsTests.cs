using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Round_trips_and_maps_keys_to_wheels()
    {
        var store = new SettingsStore(_temp.File("settings.json"));
        store.Save(new AppSettings { ConvertKey = ModifierKey.Alt, ActionsKey = ModifierKey.Shift, ExplorerOnly = false });

        var loaded = store.Load();

        Assert.Equal(OperationCategory.Convert, loaded.CategoryFor(ModifierKey.Alt));
        Assert.Equal(OperationCategory.Actions, loaded.CategoryFor(ModifierKey.Shift));
        Assert.False(loaded.ExplorerOnly);
    }

    [Fact]
    public void Falls_back_to_defaults_for_corrupt_or_ambiguous_files()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(ModifierKey.Shift, new SettingsStore(path).Load().ConvertKey);

        File.WriteAllText(path, """{"ConvertKey":"Ctrl","ToolsKey":"Ctrl","ActionsKey":"Alt"}""");
        Assert.Equal(ModifierKey.Shift, new SettingsStore(path).Load().ConvertKey);
    }

    [Fact]
    public void Subfolder_output_goes_into_a_quickey_folder()
    {
        var context = new OperationContext(OutputLocation.QuickeySubfolder);

        var output = context.OutputPathFor(_temp.File("photo.png"), "webp");

        Assert.Equal(Path.Combine(_temp.Path, "Quickey", "photo.webp"), output);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "Quickey")));
    }
}
