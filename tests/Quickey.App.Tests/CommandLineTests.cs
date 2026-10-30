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

public class UpdateHandoverTests
{
    [Theory]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("0.1.10", null, true)]      // running version predates the handshake
    [InlineData("0.1.10", "0.1.10", false)] // same version: just forward the arguments
    [InlineData("0.1.9", "0.1.10", false)]  // never let an older copy replace a newer one
    [InlineData("?", "0.1.9", false)]       // unknown own version: stay safe
    public void Only_a_newer_version_takes_over(string mine, string? running, bool takesOver) =>
        Assert.Equal(takesOver, SingleInstance.IsNewer(mine, running));

    [Fact]
    public void Parses_the_quit_request() =>
        Assert.True(CommandLine.Parse([SingleInstance.QuitForUpdateArgument]).QuitForUpdate);
}
