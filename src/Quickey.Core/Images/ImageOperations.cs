using ImageMagick;
using ImageMagick.ImageOptimizers;
using Quickey.Core.Operations;

namespace Quickey.Core.Images;

public static class ImageOperations
{
    public static IEnumerable<IOperation> All()
    {
        foreach (var target in ImageTarget.All)
            yield return new ConvertImageOperation(target);

        yield return new ResizeImageOperation("image.resize.half", "50%", image => image.Resize(new Percentage(50)));
        yield return new ResizeImageOperation("image.resize.1920", "Fit 1920", image =>
        {
            if (image.Width > 1920 || image.Height > 1920)
                image.Resize(new MagickGeometry(1920, 1920));
        });
        yield return new CompressImageOperation();
        yield return new TransformImageOperation("image.rotate.right", "Rotate", Glyphs.Rotate, "-rotated", image => image.Rotate(90));
        yield return new TransformImageOperation("image.trim", "Trim edges", Glyphs.Crop, "-trimmed", image =>
        {
            image.Trim();
            image.ResetPage();
        });
        yield return new TransformImageOperation("image.square", "Square", Glyphs.Crop, "-square", image =>
        {
            var side = Math.Min(image.Width, image.Height);
            image.Crop(new MagickGeometry(side, side), Gravity.Center);
            image.ResetPage();
        });
        yield return new TransformImageOperation("image.strip", "Strip info", Glyphs.Shield, "-clean", image => image.Strip());
        yield return new CombineImagesToPdfOperation();
    }
}

/// <summary>Converts each image to one target format. Hidden when every file is already in that format.</summary>
public sealed class ConvertImageOperation(ImageTarget target) : PerFileOperation
{
    public ImageTarget Target { get; } = target;
    public override string Id => "image.to." + Target.Extension.TrimStart('.');
    public override string Label => Target.Label;
    public override string Glyph => Target == ImageTarget.Pdf ? Glyphs.Pdf : Glyphs.Photo;
    public override OperationCategory Category => OperationCategory.Convert;

    public override bool AppliesTo(IReadOnlyList<string> files) =>
        FileKinds.All(files, FileKind.Image) && !files.All(Target.Matches);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        if (Target.Matches(file))
            return [];
        using var frames = ImageCodec.ReadFrames(file);
        var output = context.OutputPathFor(file, Target.Extension);
        ImageCodec.Write(frames, output, Target);
        return [output];
    }
}

/// <summary>Applies an in-place change to each image and saves it next to the original in the same format.</summary>
public class TransformImageOperation(string id, string label, string glyph, string suffix, Action<IMagickImage<byte>> transform) : PerFileOperation
{
    public override string Id { get; } = id;
    public override string Label { get; } = label;
    public override string Glyph { get; } = glyph;
    public override OperationCategory Category => OperationCategory.Tools;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Image);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        var target = SameFormatTarget(file);
        using var image = ImageCodec.ReadFirstFrame(file);
        transform(image);
        var output = context.OutputPathFor(file, target.Extension, suffix);
        ImageCodec.Write(image, output, target);
        return [output];
    }

    /// <summary>The writable format closest to the source; formats Quickey can only read (HEIC, PSD, SVG, TGA) become PNG.</summary>
    internal static ImageTarget SameFormatTarget(string file) =>
        ImageTarget.All.FirstOrDefault(t => t != ImageTarget.Pdf && t.Matches(file)) ?? ImageTarget.Png;
}

public sealed class ResizeImageOperation(string id, string label, Action<IMagickImage<byte>> resize)
    : TransformImageOperation(id, label, Glyphs.Resize, "-resized", resize);

/// <summary>
/// Makes images smaller on disk: lossy re-encode for JPG/WebP/AVIF, lossless optimisation for PNG/GIF,
/// and BMP/TIFF become PNG. Keeps the result only if it is actually smaller.
/// </summary>
public sealed class CompressImageOperation : PerFileOperation
{
    public override string Id => "image.compress";
    public override string Label => "Compress";
    public override string Glyph => Glyphs.Compress;
    public override OperationCategory Category => OperationCategory.Tools;

    public override bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Image);

    protected override IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken)
    {
        var target = TransformImageOperation.SameFormatTarget(file);
        if (target == ImageTarget.Bmp || target == ImageTarget.Tiff || target == ImageTarget.Ico)
            target = ImageTarget.Png;

        var output = context.OutputPathFor(file, target.Extension, "-compressed");
        if (target == ImageTarget.Png || target == ImageTarget.Gif)
        {
            if (target.Matches(file))
                File.Copy(file, output);
            else
                using (var image = ImageCodec.ReadFirstFrame(file))
                    ImageCodec.Write(image, output, target);
            new ImageOptimizer { OptimalCompression = true }.LosslessCompress(output);
        }
        else
        {
            using var frames = ImageCodec.ReadFrames(file);
            foreach (var frame in frames)
                frame.Strip();
            ImageCodec.Write(frames, output, target, ImageCodec.CompressedQuality);
        }

        if (target.Matches(file) && new FileInfo(output).Length >= new FileInfo(file).Length)
        {
            File.Delete(output);
            throw new InvalidOperationException("Already as small as it gets.");
        }
        return [output];
    }
}

/// <summary>Puts every dropped image on its own page of a single PDF, in the order they were dropped.</summary>
public sealed class CombineImagesToPdfOperation : IOperation
{
    public string Id => "image.combine.pdf";
    public string Label => "Combine PDF";
    public string Glyph => Glyphs.Merge;
    public OperationCategory Category => OperationCategory.Tools;
    public bool CountsTowardLimit => true;

    public bool AppliesTo(IReadOnlyList<string> files) => files.Count > 1 && FileKinds.All(files, FileKind.Image);

    public Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var result = new OperationResult(Label);
            using var pages = new MagickImageCollection();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var page = ImageCodec.ReadFirstFrame(file);
                    if (page.HasAlpha)
                    {
                        page.BackgroundColor = MagickColors.White;
                        page.Alpha(AlphaAction.Remove);
                    }
                    page.Quality = ImageCodec.DefaultQuality;
                    pages.Add(page);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add(new OperationError(file, ex.Message));
                }
            }
            if (pages.Count > 0)
            {
                var output = context.OutputPathFor(files[0], ".pdf", "-combined");
                pages.Write(output, MagickFormat.Pdf);
                result.Outputs.Add(output);
            }
            return result;
        }, cancellationToken);
}
