using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class CompressTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A noisy photo-like JPEG, which (unlike a gradient) is genuinely expensive to store.</summary>
    private string Photo(string name = "photo.jpg", uint width = 1200, uint height = 900)
    {
        var path = _temp.File(name);
        using var image = new MagickImage("plasma:", width, height) { Quality = 98 };
        image.Write(path);
        return path;
    }

    [Fact]
    public void Smaller_presets_give_smaller_files()
    {
        var photo = Photo();

        var sizes = Enum.GetValues<CompressPreset>()
            .Select(p => ImageCompressor.Compress(photo, new CompressOptions { Preset = p }).Data.Length)
            .ToList();

        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
        Assert.True(sizes[^1] < new FileInfo(photo).Length);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(30)]
    public void Target_size_is_met(int kilobytes)
    {
        var photo = Photo();

        var result = ImageCompressor.Compress(photo, new CompressOptions { Mode = CompressMode.TargetSize, TargetBytes = kilobytes * 1024 });

        Assert.True(result.MetTarget);
        Assert.InRange(result.Data.Length, 1, kilobytes * 1024);
    }

    [Fact]
    public void Very_small_targets_are_met_by_scaling_down()
    {
        var photo = Photo();

        var result = ImageCompressor.Compress(photo, new CompressOptions { Mode = CompressMode.TargetSize, TargetBytes = 8 * 1024 });

        Assert.True(result.MetTarget);
        Assert.True(result.Width < 1200);
    }

    [Fact]
    public void Percent_mode_shrinks_by_at_least_that_much()
    {
        var photo = Photo();
        var original = new FileInfo(photo).Length;

        var result = ImageCompressor.Compress(photo, new CompressOptions { Mode = CompressMode.Percent, ReducePercent = 60 });

        Assert.True(result.Data.Length <= original * 40 / 100);
    }

    [Fact]
    public void Resizes_and_converts_while_compressing()
    {
        var photo = Photo();

        var result = ImageCompressor.Compress(photo, new CompressOptions { MaxSide = 600, Format = ImageTarget.WebP });

        Assert.Equal((600u, 450u), (result.Width, result.Height));
        Assert.Equal(ImageTarget.WebP, result.Target);
        Assert.Equal(MagickFormat.WebP, new MagickImageInfo(result.Data).Format);
    }

    [Fact]
    public void Png_target_size_uses_a_smaller_palette_before_scaling()
    {
        var png = _temp.File("shot.png");
        using (var image = new MagickImage("plasma:", 600, 400))
            image.Write(png);
        var original = new FileInfo(png).Length;

        var result = ImageCompressor.Compress(png, new CompressOptions { Mode = CompressMode.Percent, ReducePercent = 50 });

        Assert.True(result.MetTarget);
        Assert.Equal(ImageTarget.Png, result.Target);
        Assert.True(result.Data.Length <= original / 2);
    }

    [Fact]
    public void Strips_metadata_unless_asked_to_keep_it()
    {
        var photo = Photo();
        MetadataEditor.Write(photo, photo, new MetadataChange { Make = "Canon" });

        var stripped = ImageCompressor.Compress(photo, new CompressOptions());
        var kept = ImageCompressor.Compress(photo, new CompressOptions { KeepMetadata = true });

        static string? Make(byte[] data)
        {
            using var image = new MagickImage();
            image.Ping(data);
            return image.GetExifProfile()?.GetValue(ExifTag.Make)?.Value;
        }
        Assert.Null(Make(stripped.Data));
        Assert.Equal("Canon", Make(kept.Data));
    }

    [Fact]
    public async Task Operation_writes_files_and_summarises_the_saving()
    {
        var files = new[] { Photo("a.jpg"), Photo("b.jpg") };
        var dialogs = new FakeDialogs { Compress = new CompressOptions { Mode = CompressMode.TargetSize, TargetBytes = 60 * 1024 } };
        var registry = OperationRegistry.CreateDefault(FakeDialogs.With(fake: dialogs));

        var result = await registry.Find("image.compress")!.RunAsync(files, new OperationContext(), CancellationToken.None);

        Assert.Equal([_temp.File("a-compressed.jpg"), _temp.File("b-compressed.jpg")], result.Outputs);
        Assert.Contains("→", result.Note);
        Assert.Single(dialogs.Asked); // asked once for the whole batch
    }

    [Fact]
    public async Task Cancelling_the_dialog_skips_everything()
    {
        var files = new[] { Photo("a.jpg") };
        var registry = OperationRegistry.CreateDefault(FakeDialogs.With(fake: new FakeDialogs { Compress = null }));

        var result = await registry.Find("image.compress")!.RunAsync(files, new OperationContext(), CancellationToken.None);

        Assert.Empty(result.Outputs);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Dialog_tools_replace_the_quick_ones_on_the_wheel()
    {
        var png = new[] { _temp.Image("a.png") };

        var ids = OperationRegistry.CreateDefault(FakeDialogs.With()).For(OperationCategory.Tools, png).Select(o => o.Id).ToList();

        Assert.Equal(["image.compress", "image.crop", "image.edit", "image.metadata"], ids.Take(4));
        Assert.DoesNotContain("image.strip", ids);
        Assert.DoesNotContain("image.resize.1920", ids);
    }
}
