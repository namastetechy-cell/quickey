using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class PhotoEditorTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static MagickImage Sample()
    {
        var image = new MagickImage("gradient:#c08040-#4080c0", 120, 80);
        return image;
    }

    /// <summary>Mean of one channel. Black and white makes a one-channel gray image, where every "colour" is the gray.</summary>
    private static double Mean(IMagickImage<byte> image, PixelChannel channel = PixelChannel.Red)
    {
        var statistics = image.Statistics();
        return (statistics.GetChannel(channel) ?? statistics.GetChannel(PixelChannel.Gray) ?? statistics.GetChannel(PixelChannel.Composite))!.Mean;
    }

    private static IMagickImage<byte> Edited(PhotoAdjustments adjustments)
    {
        var image = Sample();
        PhotoEditor.Apply(image, adjustments);
        return image;
    }

    [Fact]
    public void Exposure_brightens_and_darkens()
    {
        using var original = Sample();
        using var brighter = Edited(new PhotoAdjustments { Exposure = 60 });
        using var darker = Edited(new PhotoAdjustments { Exposure = -60 });

        Assert.True(Mean(brighter) > Mean(original));
        Assert.True(Mean(darker) < Mean(original));
    }

    [Fact]
    public void Full_desaturation_and_black_and_white_remove_colour()
    {
        foreach (var adjustments in new[] { new PhotoAdjustments { Saturation = -100 }, new PhotoAdjustments { Effect = PhotoEffect.BlackAndWhite } })
        {
            using var image = Edited(adjustments);
            var spread = Math.Abs(Mean(image, PixelChannel.Red) - Mean(image, PixelChannel.Blue));
            Assert.True(spread < Quantum.Max * 0.02, $"{adjustments.Effect}/{adjustments.Saturation}: R-B spread {spread}");
        }
    }

    [Fact]
    public void Warmth_shifts_red_against_blue()
    {
        using var original = Sample();
        using var warm = Edited(new PhotoAdjustments { Warmth = 80 });

        Assert.True(Mean(warm, PixelChannel.Red) > Mean(original, PixelChannel.Red));
        Assert.True(Mean(warm, PixelChannel.Blue) < Mean(original, PixelChannel.Blue));
    }

    [Fact]
    public void Vignette_darkens_corners_more_than_the_centre()
    {
        using var flat = new MagickImage(new MagickColor("#808080"), 200, 200);
        PhotoEditor.Apply(flat, new PhotoAdjustments { Vignette = 100 });
        using var pixels = flat.GetPixels();

        Assert.True(pixels.GetPixel(2, 2).GetChannel(0) < pixels.GetPixel(100, 100).GetChannel(0));
    }

    [Theory]
    [InlineData(90, 80, 120)]
    [InlineData(180, 120, 80)]
    public void Rotation_turns_the_image(int rotation, uint width, uint height)
    {
        using var image = Edited(new PhotoAdjustments { Rotation = rotation });

        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public void Every_effect_runs()
    {
        foreach (var effect in Enum.GetValues<PhotoEffect>())
        {
            using var image = Edited(new PhotoAdjustments { Effect = effect, Sharpness = 50, Contrast = 30 });
            Assert.Equal(120u, image.Width);
        }
    }

    [Fact]
    public void Preview_and_save_use_the_same_pipeline()
    {
        var file = _temp.Image("photo.png", 120, 80);
        var adjustments = new PhotoAdjustments { Exposure = 40, Effect = PhotoEffect.Sepia };

        using var source = new MagickImage(file);
        var preview = new MagickImage(PhotoEditor.RenderPreview(source, adjustments));
        var saved = PhotoEditor.Save(file, adjustments, new OperationContext());

        Assert.Equal(_temp.File("photo-edited.png"), saved);
        using var savedImage = new MagickImage(saved);
        Assert.Equal(Mean(preview), Mean(savedImage), 0);
    }

    [Fact]
    public void Default_adjustments_are_the_identity() =>
        Assert.True(new PhotoAdjustments().IsIdentity);
}
