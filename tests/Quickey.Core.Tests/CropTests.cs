using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class CropTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static void AssertInside(PixelRect r, double width, double height)
    {
        Assert.True(r.X >= -1e-9 && r.Y >= -1e-9, $"{r} starts outside");
        Assert.True(r.Right <= width + 1e-9 && r.Bottom <= height + 1e-9, $"{r} ends outside");
    }

    [Theory]
    [InlineData(1.0, 1080, 1080)]
    [InlineData(9.0 / 16, 607.5, 1080)]
    [InlineData(16.0 / 9, 1920, 1080)]
    public void Initial_box_is_the_largest_centred_box_of_that_shape(double ratio, double width, double height)
    {
        var box = CropGeometry.Initial(1920, 1080, ratio);

        Assert.Equal(width, box.Width, 6);
        Assert.Equal(height, box.Height, 6);
        Assert.Equal((1920 - width) / 2, box.X, 6);
        Assert.Equal((1080 - height) / 2, box.Y, 6);
    }

    [Fact]
    public void Freeform_starts_with_the_whole_image_and_resizes_each_side_independently()
    {
        var box = CropGeometry.Initial(1000, 800, null);
        Assert.Equal(new PixelRect(0, 0, 1000, 800), box);

        var dragged = CropGeometry.Drag(box, CropHandle.TopLeft, 100, 50, null, 1000, 800);

        Assert.Equal(new PixelRect(100, 50, 900, 750), dragged);
    }

    [Fact]
    public void Freeform_cannot_shrink_below_the_minimum_or_flip()
    {
        var box = new PixelRect(100, 100, 200, 200);

        var dragged = CropGeometry.Drag(box, CropHandle.Right, -1000, 0, null, 1000, 800);

        Assert.Equal(CropGeometry.MinSize, dragged.Width);
        Assert.Equal(100, dragged.X);
    }

    [Theory]
    [InlineData(CropHandle.BottomRight, 300, 40)]
    [InlineData(CropHandle.TopLeft, -50, -400)]
    [InlineData(CropHandle.Right, 500, 0)]
    [InlineData(CropHandle.Top, 0, -900)]
    [InlineData(CropHandle.BottomLeft, -2000, 2000)]
    public void Locked_ratio_survives_any_drag_and_stays_inside(CropHandle handle, double dx, double dy)
    {
        const double ratio = 9.0 / 16;
        var box = new PixelRect(400, 200, 270, 480);

        var dragged = CropGeometry.Drag(box, handle, dx, dy, ratio, 1920, 1080);

        Assert.Equal(ratio, dragged.Width / dragged.Height, 6);
        AssertInside(dragged, 1920, 1080);
    }

    [Fact]
    public void Locked_corner_drag_keeps_the_opposite_corner_fixed()
    {
        var box = new PixelRect(100, 100, 400, 400);

        var dragged = CropGeometry.Drag(box, CropHandle.BottomRight, 100, 20, 1, 2000, 2000);

        Assert.Equal((100.0, 100.0), (dragged.X, dragged.Y));
        Assert.Equal(500, dragged.Width, 6); // followed the larger movement
    }

    [Fact]
    public void Moving_is_clamped_to_the_image()
    {
        var box = new PixelRect(100, 100, 400, 300);

        var moved = CropGeometry.Drag(box, CropHandle.Move, 5000, -5000, null, 1000, 800);

        Assert.Equal(new PixelRect(600, 0, 400, 300), moved);
    }

    [Fact]
    public void Original_preset_uses_the_images_own_shape()
    {
        var original = AspectPreset.Common.Single(p => p.Label == "Original");

        Assert.Equal(1.5, original.RatioFor(1500, 1000));
        Assert.Null(AspectPreset.Free.RatioFor(1500, 1000));
    }

    [Fact]
    public void Crop_writes_the_selected_pixels_in_the_same_format()
    {
        var source = _temp.Image("photo.jpg", 400, 300);

        var output = CropImageOperation.Crop(source, new PixelRect(50, 20, 90, 160), new OperationContext());

        Assert.Equal(_temp.File("photo-cropped.jpg"), output);
        var info = new MagickImageInfo(output);
        Assert.Equal((90u, 160u, MagickFormat.Jpeg), (info.Width, info.Height, info.Format));
    }

    [Fact]
    public async Task Skipped_and_cancelled_files_are_counted_not_cropped()
    {
        var files = new[] { _temp.Image("a.png"), _temp.Image("b.png"), _temp.Image("c.png") };
        var prompt = new ScriptedPrompt(
            new CropDecision(CropChoice.Crop, new PixelRect(0, 0, 10, 10)),
            new CropDecision(CropChoice.Skip),
            new CropDecision(CropChoice.CancelAll));

        var result = await new CropImageOperation(prompt).RunAsync(files, new OperationContext(), CancellationToken.None);

        Assert.Equal(_temp.File("a-cropped.png"), Assert.Single(result.Outputs));
        Assert.Equal(2, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal([(0, 3), (1, 3), (2, 3)], prompt.Asked);
    }

    [Fact]
    public void Preview_is_downscaled_but_reports_the_source_size()
    {
        var source = _temp.Image("big.png", 3200, 1600);

        var preview = ImagePreview.Load(source, maxSide: 800);

        Assert.Equal((800, 400, 3200, 1600), (preview.Width, preview.Height, preview.SourceWidth, preview.SourceHeight));
        Assert.Equal(MagickFormat.Png, new MagickImageInfo(preview.Png).Format);
    }

    [Fact]
    public void Crop_only_appears_when_the_app_supplies_a_prompt()
    {
        var png = new[] { _temp.Image("a.png") };

        Assert.DoesNotContain(OperationRegistry.CreateDefault().For(OperationCategory.Tools, png), o => o.Id == "image.crop");
        Assert.Contains(OperationRegistry.CreateDefault(FakeDialogs.With(crop: new ScriptedPrompt())).For(OperationCategory.Tools, png), o => o.Id == "image.crop");
    }

    private sealed class ScriptedPrompt(params CropDecision[] decisions) : ICropPrompt
    {
        private readonly Queue<CropDecision> _decisions = new(decisions);
        public List<(int, int)> Asked { get; } = [];

        public Task<CropDecision> AskAsync(string file, int index, int count, CancellationToken cancellationToken)
        {
            Asked.Add((index, count));
            return Task.FromResult(_decisions.Dequeue());
        }
    }
}
