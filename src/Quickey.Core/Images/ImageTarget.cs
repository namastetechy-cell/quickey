using ImageMagick;

namespace Quickey.Core.Images;

/// <summary>
/// An image format Quickey can write. HEIC is read-only: the bundled codec decodes HEVC but
/// doesn't encode it (patent licensing), so HEIC photos convert from, not to.
/// </summary>
public sealed record ImageTarget(string Label, string Extension, MagickFormat Format, bool SupportsAlpha, bool SupportsAnimation)
{
    public static readonly ImageTarget Png = new("PNG", ".png", MagickFormat.Png, true, false);
    public static readonly ImageTarget Jpg = new("JPG", ".jpg", MagickFormat.Jpeg, false, false);
    public static readonly ImageTarget WebP = new("WebP", ".webp", MagickFormat.WebP, true, true);
    public static readonly ImageTarget Avif = new("AVIF", ".avif", MagickFormat.Avif, true, false);
    public static readonly ImageTarget Gif = new("GIF", ".gif", MagickFormat.Gif, true, true);
    public static readonly ImageTarget Bmp = new("BMP", ".bmp", MagickFormat.Bmp, false, false);
    public static readonly ImageTarget Tiff = new("TIFF", ".tiff", MagickFormat.Tiff, true, true);
    public static readonly ImageTarget Ico = new("ICO", ".ico", MagickFormat.Ico, true, false);
    public static readonly ImageTarget Pdf = new("PDF", ".pdf", MagickFormat.Pdf, false, true);

    public static IReadOnlyList<ImageTarget> All { get; } = [Png, Jpg, WebP, Avif, Gif, Bmp, Tiff, Ico, Pdf];

    /// <summary>True when <paramref name="path"/> is already in this format (".jpeg" counts as JPG).</summary>
    public bool Matches(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext == Extension
            || (this == Jpg && ext is ".jpeg" or ".jfif")
            || (this == Tiff && ext == ".tif");
    }
}
