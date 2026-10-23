using ImageMagick;
using Quickey.Core.Operations;

namespace Quickey.Core.Images;

public enum CropChoice
{
    Crop,
    Skip,
    CancelAll,
}

public sealed record CropDecision(CropChoice Choice, PixelRect Rect = default);

/// <summary>Asks the user where to crop one image. The Windows app shows the crop window.</summary>
public interface ICropPrompt
{
    Task<CropDecision> AskAsync(string file, int index, int count, CancellationToken cancellationToken);
}

/// <summary>
/// Opens the crop window for each image in turn and saves "photo-cropped.jpg" in the same format.
/// Animated GIF/WebP keep their animation; every frame is cropped the same way.
/// </summary>
public sealed class CropImageOperation(ICropPrompt prompt) : IOperation
{
    public string Id => "image.crop";
    public string Label => "Crop";
    public string Glyph => Glyphs.Crop;
    public OperationCategory Category => OperationCategory.Tools;
    public bool CountsTowardLimit => true;

    public bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Image);

    public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
    {
        var result = new OperationResult(Label);
        for (var i = 0; i < files.Count; i++)
        {
            var decision = await prompt.AskAsync(files[i], i, files.Count, cancellationToken);
            if (decision.Choice == CropChoice.CancelAll)
            {
                result.Skipped += files.Count - i;
                break;
            }
            if (decision.Choice == CropChoice.Skip)
            {
                result.Skipped++;
                continue;
            }

            var file = files[i];
            try
            {
                result.Outputs.Add(await Task.Run(() => Crop(file, decision.Rect, context), cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add(new OperationError(file, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Crops one image to a rectangle in its oriented pixel space. Public for tests.</summary>
    public static string Crop(string file, PixelRect rect, OperationContext context)
    {
        var target = TransformImageOperation.SameFormatTarget(file);
        using var frames = ImageCodec.ReadFrames(file);
        var (x, y, width, height) = rect.ToPixels((int)frames[0].Width, (int)frames[0].Height);
        foreach (var frame in frames)
        {
            frame.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
            frame.ResetPage();
        }
        var output = context.OutputPathFor(file, target.Extension, "-cropped");
        ImageCodec.Write(frames, output, target);
        return output;
    }
}
