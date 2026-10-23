using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class ImageOperationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly OperationRegistry _registry = OperationRegistry.CreateDefault();
    private readonly OperationContext _context = new();

    public void Dispose() => _temp.Dispose();

    private Task<OperationResult> Run(string id, params string[] files) =>
        _registry.Find(id)!.RunAsync(files, _context, CancellationToken.None);

    [Theory]
    [InlineData("image.to.jpg", ".jpg", MagickFormat.Jpeg)]
    [InlineData("image.to.webp", ".webp", MagickFormat.WebP)]
    [InlineData("image.to.avif", ".avif", MagickFormat.Avif)]
    [InlineData("image.to.gif", ".gif", MagickFormat.Gif)]
    [InlineData("image.to.bmp", ".bmp", MagickFormat.Bmp)]
    [InlineData("image.to.tiff", ".tiff", MagickFormat.Tiff)]
    [InlineData("image.to.ico", ".ico", MagickFormat.Ico)]
    [InlineData("image.to.pdf", ".pdf", MagickFormat.Pdf)]
    public async Task Converts_png_to_each_target(string id, string extension, MagickFormat expected)
    {
        var source = _temp.Image("photo.png", alpha: true);

        var result = await Run(id, source);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var output = Assert.Single(result.Outputs);
        Assert.Equal(_temp.File("photo" + extension), output);
        if (expected != MagickFormat.Pdf)
            Assert.Equal(expected, new MagickImageInfo(output).Format);
        else
            Assert.StartsWith("%PDF", File.ReadAllText(output)[..4]);
    }

    [Fact]
    public async Task Never_overwrites_existing_files()
    {
        var source = _temp.Image("photo.png");
        File.WriteAllText(_temp.File("photo.jpg"), "keep me");

        var result = await Run("image.to.jpg", source);

        Assert.Equal(_temp.File("photo (1).jpg"), Assert.Single(result.Outputs));
        Assert.Equal("keep me", File.ReadAllText(_temp.File("photo.jpg")));
    }

    [Fact]
    public void Hides_conversion_to_the_format_the_files_already_have()
    {
        var files = new[] { _temp.Image("a.jpg"), _temp.Image("b.jpeg") };

        var labels = _registry.For(OperationCategory.Convert, files).Select(o => o.Label).ToList();

        Assert.DoesNotContain("JPG", labels);
        Assert.Contains("PNG", labels);
    }

    [Fact]
    public void Offers_nothing_for_mixed_or_unknown_files()
    {
        var files = new[] { _temp.Image("a.png"), _temp.File("notes.xyz") };

        Assert.Empty(_registry.For(OperationCategory.Convert, files));
        Assert.Empty(_registry.For(OperationCategory.Tools, files));
    }

    [Fact]
    public async Task Resizes_to_half()
    {
        var source = _temp.Image("big.png", 400, 200);

        var result = await Run("image.resize.half", source);

        var info = new MagickImageInfo(Assert.Single(result.Outputs));
        Assert.Equal((200u, 100u), (info.Width, info.Height));
    }

    [Fact]
    public async Task Fit_1920_leaves_small_images_alone_and_shrinks_large_ones()
    {
        var small = _temp.Image("small.png", 800, 600);
        var large = _temp.Image("large.png", 3840, 1000);

        var result = await Run("image.resize.1920", small, large);

        Assert.Equal((800u, 600u), Size(result.Outputs[0]));
        Assert.Equal(1920u, Size(result.Outputs[1]).Width);
    }

    [Fact]
    public async Task Rotate_swaps_dimensions()
    {
        var source = _temp.Image("wide.jpg", 300, 120);

        var result = await Run("image.rotate.right", source);

        var output = Assert.Single(result.Outputs);
        Assert.EndsWith("wide-rotated.jpg", output);
        Assert.Equal((120u, 300u), Size(output));
    }

    [Fact]
    public async Task Compress_shrinks_a_high_quality_jpeg()
    {
        var source = _temp.File("photo.jpg");
        using (var image = new MagickImage("plasma:", 800, 600) { Quality = 100 })
            image.Write(source);

        var result = await Run("image.compress", source);

        var output = Assert.Single(result.Outputs);
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length);
    }

    [Fact]
    public async Task Combines_images_into_one_pdf_page_each()
    {
        var files = new[] { _temp.Image("1.png"), _temp.Image("2.jpg"), _temp.Image("3.webp") };

        var result = await Run("image.combine.pdf", files);

        var output = Assert.Single(result.Outputs);
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, pdf.PageCount);
    }

    [Fact]
    public async Task One_bad_file_does_not_stop_the_batch()
    {
        var good = _temp.Image("good.png");
        var bad = _temp.File("bad.png");
        File.WriteAllText(bad, "not an image");

        var result = await Run("image.to.jpg", bad, good);

        Assert.Single(result.Errors);
        Assert.Equal(_temp.File("good.jpg"), Assert.Single(result.Outputs));
    }

    private static (uint Width, uint Height) Size(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }
}
