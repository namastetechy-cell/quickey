using ImageMagick;

namespace Quickey.Core.Images;

/// <summary>Shared read/write helpers so every image operation handles orientation, alpha and animation the same way.</summary>
public static class ImageCodec
{
    public const uint DefaultQuality = 85;
    public const uint CompressedQuality = 72;

    public static MagickImageCollection ReadFrames(string path)
    {
        var frames = new MagickImageCollection(path);
        if (frames.Count > 1)
            frames.Coalesce();
        foreach (var frame in frames)
            frame.AutoOrient();
        return frames;
    }

    public static MagickImage ReadFirstFrame(string path)
    {
        using var frames = ReadFrames(path);
        return (MagickImage)frames[0].Clone();
    }

    public static void Write(MagickImageCollection frames, string path, ImageTarget target, uint quality = DefaultQuality)
    {
        if (target.SupportsAnimation && frames.Count > 1)
        {
            foreach (var frame in frames)
                Prepare(frame, target, quality);
            if (target == ImageTarget.Gif)
                frames.Optimize();
            frames.Write(path, target.Format);
            return;
        }

        using var image = frames[0].Clone();
        Write(image, path, target, quality);
    }

    public static void Write(IMagickImage<byte> image, string path, ImageTarget target, uint quality = DefaultQuality)
    {
        Prepare(image, target, quality);
        if (target == ImageTarget.Ico)
        {
            // An .ico holds several sizes; Windows picks the best one for each context.
            if (image.Width > 256 || image.Height > 256)
                image.Resize(new MagickGeometry(256, 256));
            image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
        }
        image.Write(path, target.Format);
    }

    private static void Prepare(IMagickImage<byte> image, ImageTarget target, uint quality)
    {
        if (!target.SupportsAlpha && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        image.Quality = quality;
    }
}
