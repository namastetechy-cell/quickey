using ImageMagick;
using ImageMagick.ImageOptimizers;

namespace Quickey.Core.Images;

public enum CompressMode
{
    /// <summary>A named quality level.</summary>
    Preset,

    /// <summary>Each image must come out at or under a byte size.</summary>
    TargetSize,

    /// <summary>Each image must come out this much smaller than it was.</summary>
    Percent,
}

public enum CompressPreset
{
    HighQuality,
    Balanced,
    Small,
    Smallest,
}

public sealed record CompressOptions
{
    public CompressMode Mode { get; init; } = CompressMode.Preset;
    public CompressPreset Preset { get; init; } = CompressPreset.Balanced;
    public long TargetBytes { get; init; } = 500 * 1024;
    public int ReducePercent { get; init; } = 50;

    /// <summary>Resize while compressing: the longest side, in pixels. Null keeps the size.</summary>
    public int? MaxSide { get; init; }

    /// <summary>Convert while compressing (JPG, WebP, AVIF...). Null keeps the format.</summary>
    public ImageTarget? Format { get; init; }

    /// <summary>Keep date, camera and location. Off by default: metadata is dead weight in a compressed copy.</summary>
    public bool KeepMetadata { get; init; }
}

public sealed record CompressResult(byte[] Data, ImageTarget Target, uint Width, uint Height, long OriginalBytes, bool MetTarget);

/// <summary>
/// Shrinks images by quality, by a target size, or by a percentage, optionally resizing and
/// converting on the way. Target sizes are met by binary-searching the encoder quality, and if even
/// the lowest acceptable quality is too big, by scaling the image down until it fits.
/// </summary>
public static class ImageCompressor
{
    public const uint MinQuality = 20;
    public const uint MaxQuality = 92;
    private const int MaxDownscaleRounds = 8;

    public static uint QualityFor(CompressPreset preset) => preset switch
    {
        CompressPreset.HighQuality => 85,
        CompressPreset.Balanced => 72,
        CompressPreset.Small => 55,
        _ => 40,
    };

    /// <summary>The format a file is compressed into: the requested one, else its own (BMP/TIFF/ICO become PNG).</summary>
    public static ImageTarget TargetFor(string file, ImageTarget? requested)
    {
        if (requested is not null)
            return requested;
        var own = TransformImageOperation.SameFormatTarget(file);
        return own == ImageTarget.Bmp || own == ImageTarget.Tiff || own == ImageTarget.Ico ? ImageTarget.Png : own;
    }

    public static bool IsLossy(ImageTarget target) =>
        target == ImageTarget.Jpg || target == ImageTarget.WebP || target == ImageTarget.Avif;

    public static CompressResult Compress(string file, CompressOptions options, CancellationToken cancellationToken = default)
    {
        var originalBytes = new FileInfo(file).Length;
        var target = TargetFor(file, options.Format);
        long? limit = options.Mode switch
        {
            CompressMode.TargetSize => Math.Max(1024, options.TargetBytes),
            CompressMode.Percent => Math.Max(1024, originalBytes * (100 - Math.Clamp(options.ReducePercent, 1, 99)) / 100),
            _ => null,
        };

        using var frames = ImageCodec.ReadFrames(file);
        foreach (var frame in frames)
        {
            if (options.MaxSide is { } side && (frame.Width > side || frame.Height > side))
                frame.Resize(new MagickGeometry((uint)side, (uint)side));
            if (!options.KeepMetadata)
                frame.Strip();
        }

        // Animations: one pass at the chosen quality; searching per-frame sizes isn't worth the time.
        if (frames.Count > 1 && target.SupportsAnimation)
        {
            var quality = limit is null ? QualityFor(options.Preset) : QualityFor(CompressPreset.Small);
            var data = EncodeFrames(frames, target, quality, options.Preset >= CompressPreset.Small);
            return new CompressResult(data, target, frames[0].Width, frames[0].Height, originalBytes, limit is null || data.Length <= limit);
        }

        using var image = (MagickImage)frames[0].Clone();
        byte[] result;
        bool met;
        if (limit is null)
        {
            result = IsLossy(target)
                ? Encode(image, target, QualityFor(options.Preset))
                : EncodeLossless(image, target, options.Preset switch { CompressPreset.Small => 256, CompressPreset.Smallest => 64, _ => 0 });
            met = true;
        }
        else
        {
            (result, met) = FitUnder(image, target, limit.Value, cancellationToken);
        }
        using var info = new MagickImage();
        info.Ping(result);
        return new CompressResult(result, target, info.Width, info.Height, originalBytes, met);
    }

    /// <summary>Best-looking encoding at or under <paramref name="limit"/> bytes, scaling down only as a last resort.</summary>
    private static (byte[] Data, bool Met) FitUnder(MagickImage image, ImageTarget target, long limit, CancellationToken cancellationToken)
    {
        for (var round = 0; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] smallest;
            if (IsLossy(target))
            {
                // Highest quality that fits: quality → size is monotonic enough for a binary search.
                byte[]? best = null;
                uint low = MinQuality, high = MaxQuality;
                while (low <= high)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mid = (low + high) / 2;
                    var data = Encode(image, target, mid);
                    if (data.Length <= limit) { best = data; low = mid + 1; }
                    else { high = mid - 1; }
                }
                if (best is not null)
                    return (best, true);
                smallest = Encode(image, target, MinQuality);
            }
            else
            {
                foreach (var colors in new[] { 0, 256, 64 })
                {
                    var data = EncodeLossless(image, target, colors);
                    if (data.Length <= limit)
                        return (data, true);
                }
                smallest = EncodeLossless(image, target, 64);
            }

            if (round == MaxDownscaleRounds || image.Width <= 32 || image.Height <= 32)
                return (smallest, false);
            // Bytes scale roughly with pixel count, so shrink each side by the square root of the overshoot.
            var factor = Math.Clamp(Math.Sqrt((double)limit / smallest.Length) * 0.95, 0.3, 0.9);
            image.Resize(new Percentage(factor * 100));
        }
    }

    private static byte[] Encode(MagickImage image, ImageTarget target, uint quality)
    {
        using var copy = (MagickImage)image.Clone();
        if (!target.SupportsAlpha && copy.HasAlpha)
        {
            copy.BackgroundColor = MagickColors.White;
            copy.Alpha(AlphaAction.Remove);
        }
        copy.Quality = quality;
        return copy.ToByteArray(target.Format);
    }

    /// <param name="colors">0 for true lossless; otherwise reduce to this palette size first (much smaller PNGs).</param>
    private static byte[] EncodeLossless(MagickImage image, ImageTarget target, int colors)
    {
        using var copy = (MagickImage)image.Clone();
        if (colors > 0)
            copy.Quantize(new QuantizeSettings { Colors = (uint)colors, DitherMethod = DitherMethod.FloydSteinberg });
        copy.Quality = 95; // PNG: zlib level 9 with adaptive filtering
        var data = copy.ToByteArray(target.Format);
        if (target != ImageTarget.Png && target != ImageTarget.Gif)
            return data;
        using var stream = new MemoryStream();
        stream.Write(data);
        stream.Position = 0;
        new ImageOptimizer { OptimalCompression = true }.LosslessCompress(stream);
        return stream.ToArray();
    }

    private static byte[] EncodeFrames(MagickImageCollection frames, ImageTarget target, uint quality, bool reducePalette)
    {
        foreach (var frame in frames)
        {
            frame.Quality = quality;
            if (reducePalette && target == ImageTarget.Gif)
                frame.Quantize(new QuantizeSettings { Colors = 128 });
        }
        if (target == ImageTarget.Gif)
            frames.Optimize();
        return frames.ToByteArray(target.Format);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };
}
