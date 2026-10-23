namespace Quickey.Core.Images;

/// <summary>A rectangle in image pixels (after EXIF orientation is applied).</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Rounded to whole pixels and kept inside the image.</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int imageWidth, int imageHeight)
    {
        var x = Math.Clamp((int)Math.Round(X), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Round(Y), 0, imageHeight - 1);
        var width = Math.Clamp((int)Math.Round(Width), 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Round(Height), 1, imageHeight - y);
        return (x, y, width, height);
    }
}

/// <summary>A crop shape choice. <see cref="Ratio"/> is width ÷ height; null means freeform.</summary>
public sealed record AspectPreset(string Label, double? Ratio)
{
    public static readonly AspectPreset Free = new("Free", null);

    /// <summary>The choices offered in the crop window. "Original" is resolved per image.</summary>
    public static IReadOnlyList<AspectPreset> Common { get; } =
    [
        Free,
        new("Original", 0),
        new("1:1", 1),
        new("4:5", 4.0 / 5),
        new("3:4", 3.0 / 4),
        new("9:16", 9.0 / 16),
        new("16:9", 16.0 / 9),
        new("4:3", 4.0 / 3),
        new("3:2", 3.0 / 2),
        new("2:3", 2.0 / 3),
    ];

    /// <summary>The ratio to enforce for an image of this size, or null for freeform.</summary>
    public double? RatioFor(double imageWidth, double imageHeight) =>
        Ratio == 0 ? imageWidth / imageHeight : Ratio;
}

/// <summary>Which part of the crop box is being dragged.</summary>
[Flags]
public enum CropHandle
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    Move = 16,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
}

/// <summary>
/// The crop box maths, kept free of UI so it can be tested: the box always stays inside the image,
/// never gets smaller than <see cref="MinSize"/>, and keeps the chosen aspect ratio while resizing.
/// </summary>
public static class CropGeometry
{
    public const double MinSize = 16;

    /// <summary>The largest box of the given ratio, centred in the image. Freeform gets the whole image.</summary>
    public static PixelRect Initial(double imageWidth, double imageHeight, double? ratio)
    {
        if (ratio is not { } r)
            return new PixelRect(0, 0, imageWidth, imageHeight);
        var width = Math.Min(imageWidth, imageHeight * r);
        var height = width / r;
        return new PixelRect((imageWidth - width) / 2, (imageHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Applies a drag of (<paramref name="dx"/>, <paramref name="dy"/>) image pixels to <paramref name="start"/>,
    /// the box as it was when the drag began.
    /// </summary>
    public static PixelRect Drag(PixelRect start, CropHandle handle, double dx, double dy, double? ratio, double imageWidth, double imageHeight)
    {
        if (handle == CropHandle.Move)
        {
            var x = Math.Clamp(start.X + dx, 0, imageWidth - start.Width);
            var y = Math.Clamp(start.Y + dy, 0, imageHeight - start.Height);
            return start with { X = x, Y = y };
        }
        if (handle == CropHandle.None)
            return start;

        return ratio is { } r
            ? DragLocked(start, handle, dx, dy, r, imageWidth, imageHeight)
            : DragFree(start, handle, dx, dy, imageWidth, imageHeight);
    }

    private static PixelRect DragFree(PixelRect s, CropHandle handle, double dx, double dy, double imageWidth, double imageHeight)
    {
        double left = s.X, top = s.Y, right = s.Right, bottom = s.Bottom;
        if (handle.HasFlag(CropHandle.Left))
            left = Math.Clamp(s.X + dx, 0, right - MinSize);
        if (handle.HasFlag(CropHandle.Right))
            right = Math.Clamp(s.Right + dx, left + MinSize, imageWidth);
        if (handle.HasFlag(CropHandle.Top))
            top = Math.Clamp(s.Y + dy, 0, bottom - MinSize);
        if (handle.HasFlag(CropHandle.Bottom))
            bottom = Math.Clamp(s.Bottom + dy, top + MinSize, imageHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static PixelRect DragLocked(PixelRect s, CropHandle handle, double dx, double dy, double r, double imageWidth, double imageHeight)
    {
        var horizontal = handle.HasFlag(CropHandle.Left) || handle.HasFlag(CropHandle.Right);
        var vertical = handle.HasFlag(CropHandle.Top) || handle.HasFlag(CropHandle.Bottom);

        // The fixed point the box grows from: the opposite corner, or the middle of the opposite edge.
        var anchorX = handle.HasFlag(CropHandle.Left) ? s.Right : handle.HasFlag(CropHandle.Right) ? s.X : s.X + s.Width / 2;
        var anchorY = handle.HasFlag(CropHandle.Top) ? s.Bottom : handle.HasFlag(CropHandle.Bottom) ? s.Y : s.Y + s.Height / 2;

        var wantedWidth = horizontal ? s.Width + (handle.HasFlag(CropHandle.Left) ? -dx : dx) : 0;
        var wantedHeight = vertical ? s.Height + (handle.HasFlag(CropHandle.Top) ? -dy : dy) : 0;
        // Corners follow whichever direction the pointer moved further; edges follow their own axis.
        var width = Math.Max(wantedWidth, wantedHeight * r);

        // Room available from the anchor, in each direction the box can grow.
        var roomX = handle.HasFlag(CropHandle.Left) ? anchorX
            : handle.HasFlag(CropHandle.Right) ? imageWidth - anchorX
            : 2 * Math.Min(anchorX, imageWidth - anchorX);
        var roomY = handle.HasFlag(CropHandle.Top) ? anchorY
            : handle.HasFlag(CropHandle.Bottom) ? imageHeight - anchorY
            : 2 * Math.Min(anchorY, imageHeight - anchorY);
        width = Math.Min(width, Math.Min(roomX, roomY * r));
        width = Math.Max(width, Math.Min(MinSize * Math.Max(1, r), Math.Min(roomX, roomY * r)));
        var height = width / r;

        var x = handle.HasFlag(CropHandle.Left) ? anchorX - width : handle.HasFlag(CropHandle.Right) ? anchorX : anchorX - width / 2;
        var y = handle.HasFlag(CropHandle.Top) ? anchorY - height : handle.HasFlag(CropHandle.Bottom) ? anchorY : anchorY - height / 2;
        return new PixelRect(x, y, width, height);
    }
}
