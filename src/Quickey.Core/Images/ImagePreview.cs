using ImageMagick;

namespace Quickey.Core.Images;

/// <summary>
/// A display copy of an image for the crop window. Decoded by the same codec as the conversion,
/// with EXIF orientation applied, so what you see is exactly what gets cropped (and HEIC, AVIF
/// and WebP preview even where Windows has no codec for them).
/// </summary>
public sealed record ImagePreview(byte[] Png, int Width, int Height, int SourceWidth, int SourceHeight)
{
    public static ImagePreview Load(string path, int maxSide = 1600)
    {
        using var image = ImageCodec.ReadFirstFrame(path);
        int sourceWidth = (int)image.Width, sourceHeight = (int)image.Height;
        if (image.Width > maxSide || image.Height > maxSide)
            image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        var png = image.ToByteArray(MagickFormat.Png);
        return new ImagePreview(png, (int)image.Width, (int)image.Height, sourceWidth, sourceHeight);
    }
}
