using Quickey.App.Services;

namespace Quickey.App.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parses_explorer_launches()
    {
        var command = CommandLine.Parse(["--files", @"C:\a b\photo.png", @"C:\x.pdf"]);

        Assert.Equal([@"C:\a b\photo.png", @"C:\x.pdf"], command.Files);
        Assert.False(command.Background);
    }

    [Fact]
    public void Parses_startup_and_settings_flags()
    {
        Assert.True(CommandLine.Parse(["--background"]).Background);
        Assert.True(CommandLine.Parse(["--settings"]).OpenSettings);
        Assert.Empty(CommandLine.Parse([]).Files);
    }
}
