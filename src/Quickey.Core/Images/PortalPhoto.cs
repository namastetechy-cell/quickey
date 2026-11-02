using ImageMagick;
using Quickey.Core.Operations;

namespace Quickey.Core.Images;

/// <summary>
/// The rules an upload portal sets for a photo or signature: an exact size (pixels, or centimetres
/// at a DPI) and a file-size window. Built-in presets cover the common ones; Custom takes any values.
/// </summary>
public sealed record PortalPreset(string Name, string Suffix, int WidthPx, int HeightPx, int Dpi, int MinKb, int MaxKb)
{
    public const double CmPerInch = 2.54;

    public static readonly PortalPreset Passport = FromCm("Passport photo 3.5 × 4.5 cm", "-passport", 3.5, 4.5, 300, 10, 50);
    public static readonly PortalPreset ExamPhoto = new("Photo 200 × 230 px", "-photo", 200, 230, 200, 20, 50);
    public static readonly PortalPreset Signature = new("Signature 140 × 60 px", "-signature", 140, 60, 200, 10, 20);

    public static IReadOnlyList<PortalPreset> BuiltIn { get; } = [Passport, ExamPhoto, Signature];

    /// <summary>Width ÷ height, the shape the crop box is locked to.</summary>
    public double Ratio => (double)WidthPx / HeightPx;

    public long MinBytes => MinKb * 1024L;
    public long MaxBytes => MaxKb * 1024L;

    /// <summary>A preset given in centimetres, e.g. 3.5 × 4.5 cm at 300 DPI = 413 × 531 px.</summary>
    public static PortalPreset FromCm(string name, string suffix, double widthCm, double heightCm, int dpi, int minKb, int maxKb) =>
        new(name, suffix, CmToPx(widthCm, dpi), CmToPx(heightCm, dpi), dpi, minKb, maxKb);

    public static int CmToPx(double cm, int dpi) => (int)Math.Round(cm / CmPerInch * dpi);

    /// <summary>Checks a custom preset; returns a message for the first problem, or null if it's usable.</summary>
    public string? Problem() =>
        WidthPx is < 20 or > 6000 || HeightPx is < 20 or > 6000 ? "Width and height must be between 20 and 6000 pixels."
        : Dpi is < 50 or > 1200 ? "DPI must be between 50 and 1200."
        : MaxKb < 5 ? "The maximum size must be at least 5 KB."
        : MinKb < 0 || MinKb >= MaxKb ? "The minimum size must be below the maximum."
        : null;
}

public sealed record PortalResult(byte[] Jpeg, int Quality, bool TooBig, bool TooSmall)
{
    public long Bytes => Jpeg.Length;
}

/// <summary>
/// Makes a portal-ready JPEG from a photo: crop to the chosen box, resize to the exact pixels, white
/// background, metadata removed (the rotation is applied first), the DPI recorded, and the highest
/// JPEG quality that fits under the maximum size.
/// </summary>
public static class PortalPhoto
{
    public static PortalResult Make(string file, PixelRect crop, PortalPreset preset)
    {
        using var image = ImageCodec.ReadFirstFrame(file); // EXIF rotation already applied
        var (x, y, w, h) = crop.ToPixels((int)image.Width, (int)image.Height);
        image.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
        image.ResetPage();
        image.Resize(new MagickGeometry((uint)preset.WidthPx, (uint)preset.HeightPx) { IgnoreAspectRatio = true });
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        image.Strip();
        image.Density = new Density(preset.Dpi, preset.Dpi, DensityUnit.PixelsPerInch);
        image.Format = MagickFormat.Jpeg;

        // Highest quality under the maximum: quality → size is monotonic enough for a binary search.
        byte[]? best = null;
        var bestQuality = 0;
        int low = 10, high = 100;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var data = Encode(image, mid);
            if (data.Length <= preset.MaxBytes) { best = data; bestQuality = mid; low = mid + 1; }
            else { high = mid - 1; }
        }

        if (best is null)
            return new PortalResult(Encode(image, 10), 10, TooBig: true, TooSmall: false);
        // Some portals also reject files that are too small; full quality is all that honestly adds bytes.
        return new PortalResult(best, bestQuality, TooBig: false, TooSmall: best.Length < preset.MinBytes);
    }

    private static byte[] Encode(MagickImage image, int quality)
    {
        image.Quality = (uint)quality;
        // Full colour resolution at high quality; it also keeps small faces sharp.
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", quality >= 90 ? "1x1" : "2x2");
        return image.ToByteArray();
    }

    public static string Describe(PortalPreset preset, PortalResult result) =>
        $"{preset.WidthPx} × {preset.HeightPx} px · {preset.Dpi} DPI · {ImageCompressor.FormatBytes(result.Bytes)}"
        + (result.TooBig ? $" (can't get under {preset.MaxKb} KB)"
            : result.TooSmall ? $" (below the {preset.MinKb} KB minimum)"
            : "");
}

public enum PortalAction
{
    Save,
    Skip,
    CancelAll,
}

public sealed record PortalChoice(PortalAction Action, PortalPreset? Preset = null, PixelRect Crop = default);

/// <summary>Asks which preset and where to crop for one image. The Windows app shows the portal photo window.</summary>
public interface IPortalPrompt
{
    Task<PortalChoice> AskAsync(string file, int index, int count, CancellationToken cancellationToken);
}

/// <summary>Tools wheel → Portal photo: one finished upload-ready JPEG per image ("photo-passport.jpg").</summary>
public sealed class PortalPhotoOperation(IPortalPrompt prompt) : IOperation
{
    public string Id => "image.portal";
    public string Label => "Portal photo";
    public string Glyph => Glyphs.Contact;
    public OperationCategory Category => OperationCategory.Tools;
    public bool CountsTowardLimit => true;

    public bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Image);

    public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
    {
        var result = new OperationResult(Label);
        var notes = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var choice = await prompt.AskAsync(files[i], i, files.Count, cancellationToken);
            if (choice.Action == PortalAction.CancelAll)
            {
                result.Skipped += files.Count - i;
                break;
            }
            if (choice.Action == PortalAction.Skip || choice.Preset is null)
            {
                result.Skipped++;
                continue;
            }

            var file = files[i];
            try
            {
                var made = await Task.Run(() => PortalPhoto.Make(file, choice.Crop, choice.Preset), cancellationToken);
                var output = context.OutputPathFor(file, ".jpg", choice.Preset.Suffix);
                await File.WriteAllBytesAsync(output, made.Jpeg, cancellationToken);
                result.Outputs.Add(output);
                notes.Add(PortalPhoto.Describe(choice.Preset, made));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add(new OperationError(file, ex.Message));
            }
        }
        if (notes.Count == 1)
            result.Note = notes[0];
        return result;
    }
}
