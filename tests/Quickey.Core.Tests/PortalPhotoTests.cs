using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class PortalPhotoTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A detailed 12 MP-ish phone photo, with camera metadata, that is far over any portal limit.</summary>
    private string PhonePhoto(string name = "selfie.jpg", uint width = 3000, uint height = 4000)
    {
        var path = _temp.File(name);
        using var image = new MagickImage("plasma:", width, height) { Quality = 95 };
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Phone");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.SetProfile(exif);
        image.Write(path);
        return path;
    }

    private static PixelRect Box(PortalPreset preset, uint width, uint height) => CropGeometry.Initial(width, height, preset.Ratio);

    [Fact]
    public void Passport_preset_is_3_5_by_4_5_cm_at_300_dpi()
    {
        Assert.Equal((413, 531, 300), (PortalPreset.Passport.WidthPx, PortalPreset.Passport.HeightPx, PortalPreset.Passport.Dpi));
        Assert.Equal(35.0 / 45, PortalPreset.Passport.Ratio, 2);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_preset_gives_exact_pixels_dpi_and_size(PortalPreset preset)
    {
        var photo = PhonePhoto();

        var result = PortalPhoto.Make(photo, Box(preset, 3000, 4000), preset);

        Assert.False(result.TooBig);
        Assert.InRange(result.Bytes, 1, preset.MaxBytes);
        using var image = new MagickImage(result.Jpeg);
        Assert.Equal((MagickFormat.Jpeg, (uint)preset.WidthPx, (uint)preset.HeightPx), (image.Format, image.Width, image.Height));
        Assert.Equal(preset.Dpi, image.Density.X, 0);
        Assert.Null(image.GetExifProfile()?.GetValue(ExifTag.Make)); // camera and location info removed
    }

    public static TheoryData<PortalPreset> Presets()
    {
        var data = new TheoryData<PortalPreset>();
        foreach (var preset in PortalPreset.BuiltIn)
            data.Add(preset);
        return data;
    }

    [Fact]
    public void Uses_the_highest_quality_that_fits()
    {
        var photo = PhonePhoto();
        var generous = PortalPreset.Passport with { MaxKb = 400 };

        var tight = PortalPhoto.Make(photo, Box(PortalPreset.Passport, 3000, 4000), PortalPreset.Passport);
        var loose = PortalPhoto.Make(photo, Box(generous, 3000, 4000), generous);

        Assert.True(loose.Quality > tight.Quality);
    }

    [Fact]
    public void Flags_a_file_that_ends_up_under_the_minimum()
    {
        var plain = _temp.File("plain.png");
        using (var image = new MagickImage(new MagickColor("#ffffff"), 800, 400))
            image.Write(plain);

        var result = PortalPhoto.Make(plain, Box(PortalPreset.Signature, 800, 400), PortalPreset.Signature);

        Assert.True(result.TooSmall); // a blank page can't honestly be made 10 KB
        Assert.Contains("minimum", PortalPhoto.Describe(PortalPreset.Signature, result));
    }

    [Fact]
    public void Transparent_signatures_get_a_white_background()
    {
        var png = _temp.File("sign.png");
        using (var image = new MagickImage(MagickColors.Transparent, 700, 300))
            image.Write(png);

        var result = PortalPhoto.Make(png, Box(PortalPreset.Signature, 700, 300), PortalPreset.Signature);

        using var output = new MagickImage(result.Jpeg);
        using var pixels = output.GetPixels();
        Assert.True(pixels.GetPixel(5, 5).ToColor()!.R > 240);
    }

    [Fact]
    public void Custom_presets_convert_cm_and_are_validated()
    {
        var stamp = PortalPreset.FromCm("Stamp", "-stamp", 2, 2.5, 300, 5, 30);
        Assert.Equal((236, 295), (stamp.WidthPx, stamp.HeightPx));
        Assert.Null(stamp.Problem());

        Assert.NotNull((stamp with { MinKb = 40 }).Problem());
        Assert.NotNull((stamp with { WidthPx = 5 }).Problem());
        Assert.NotNull((stamp with { Dpi = 10 }).Problem());
    }

    [Fact]
    public async Task Operation_saves_a_named_jpeg_and_skips_on_request()
    {
        var files = new[] { PhonePhoto("a.jpg", 1200, 1600), PhonePhoto("b.jpg", 1200, 1600) };
        var prompt = new ScriptedPortal(
            new PortalChoice(PortalAction.Save, PortalPreset.Passport, Box(PortalPreset.Passport, 1200, 1600)),
            new PortalChoice(PortalAction.Skip));

        var result = await new PortalPhotoOperation(prompt).RunAsync(files, new OperationContext(), CancellationToken.None);

        Assert.Equal(_temp.File("a-passport.jpg"), Assert.Single(result.Outputs));
        Assert.Equal(1, result.Skipped);
        Assert.Contains("413 × 531 px", result.Note);
    }

    [Fact]
    public void Appears_on_the_tools_wheel_right_after_compress()
    {
        var dialogs = FakeDialogs.With() with { Portal = new ScriptedPortal() };
        var ids = OperationRegistry.CreateDefault(dialogs).For(OperationCategory.Tools, [_temp.Image("a.png")]).Select(o => o.Id).ToList();

        Assert.Equal(["image.compress", "image.portal", "image.crop"], ids.Take(3));
    }

    private sealed class ScriptedPortal(params PortalChoice[] choices) : IPortalPrompt
    {
        private readonly Queue<PortalChoice> _choices = new(choices);

        public Task<PortalChoice> AskAsync(string file, int index, int count, CancellationToken cancellationToken) =>
            Task.FromResult(_choices.Dequeue());
    }
}
