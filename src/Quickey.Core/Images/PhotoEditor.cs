using ImageMagick;

namespace Quickey.Core.Images;

public enum PhotoEffect
{
    None,
    BlackAndWhite,
    Sepia,
    Vintage,
    Vivid,
    Cool,
    Warm,
    Fade,
}

/// <summary>
/// Photo adjustments, each -100..100 (Sharpness and Vignette 0..100), applied in a fixed order so
/// the preview and the saved file always match.
/// </summary>
public sealed record PhotoAdjustments
{
    public int Exposure { get; init; }
    public int Contrast { get; init; }
    public int Saturation { get; init; }
    public int Warmth { get; init; }
    public int Sharpness { get; init; }
    public int Vignette { get; init; }
    public PhotoEffect Effect { get; init; }

    /// <summary>Clockwise, in quarter turns: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    public bool FlipHorizontal { get; init; }

    public bool IsIdentity => this == new PhotoAdjustments();
}

public static class PhotoEditor
{
    public static void Apply(IMagickImage<byte> image, PhotoAdjustments a)
    {
        if (a.FlipHorizontal)
            image.Flop();
        if (a.Rotation % 360 != 0)
            image.Rotate(a.Rotation % 360);

        // Effects first, so the sliders fine-tune the look rather than being overridden by it.
        switch (a.Effect)
        {
            case PhotoEffect.BlackAndWhite:
                image.Grayscale();
                break;
            case PhotoEffect.Sepia:
                image.SepiaTone(new Percentage(80));
                break;
            case PhotoEffect.Vintage:
                image.SepiaTone(new Percentage(90));
                image.Modulate(new Percentage(100), new Percentage(60), new Percentage(100));
                Fade(image, 0.10);
                ApplyVignette(image, 45);
                break;
            case PhotoEffect.Vivid:
                image.Modulate(new Percentage(100), new Percentage(140), new Percentage(100));
                image.BrightnessContrast(new Percentage(0), new Percentage(12));
                break;
            case PhotoEffect.Cool:
                ApplyWarmth(image, -35);
                break;
            case PhotoEffect.Warm:
                ApplyWarmth(image, 35);
                break;
            case PhotoEffect.Fade:
                Fade(image, 0.18);
                image.Modulate(new Percentage(100), new Percentage(80), new Percentage(100));
                break;
        }

        if (a.Exposure != 0)
            // ±100 is ±1 stop.
            image.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, a.Exposure / 100.0));
        if (a.Contrast != 0)
            image.BrightnessContrast(new Percentage(0), new Percentage(a.Contrast * 0.6));
        if (a.Saturation != 0)
            image.Modulate(new Percentage(100), new Percentage(100 + a.Saturation), new Percentage(100));
        if (a.Warmth != 0)
            ApplyWarmth(image, a.Warmth);
        if (a.Sharpness > 0)
            image.Sharpen(0, a.Sharpness / 40.0);
        if (a.Vignette > 0)
            ApplyVignette(image, a.Vignette);
    }

    /// <summary>A quick PNG of the adjusted preview for the editor window.</summary>
    public static byte[] RenderPreview(MagickImage preview, PhotoAdjustments adjustments)
    {
        using var copy = (MagickImage)preview.Clone();
        Apply(copy, adjustments);
        copy.Quality = 10; // fastest zlib level: this is a throwaway preview
        return copy.ToByteArray(MagickFormat.Png);
    }

    /// <summary>Applies the adjustments to the full-size image(s) and saves "photo-edited.jpg" in the same format.</summary>
    public static string Save(string file, PhotoAdjustments adjustments, Operations.OperationContext context)
    {
        var target = TransformImageOperation.SameFormatTarget(file);
        using var frames = ImageCodec.ReadFrames(file);
        foreach (var frame in frames)
            Apply(frame, adjustments);
        var output = context.OutputPathFor(file, target.Extension, "-edited");
        ImageCodec.Write(frames, output, target, quality: 92);
        return output;
    }

    /// <summary>Positive is warmer (more red, less blue); ±100 is a strong shift.</summary>
    private static void ApplyWarmth(IMagickImage<byte> image, int warmth)
    {
        var amount = warmth / 400.0;
        image.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + amount);
        image.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - amount);
    }

    /// <summary>Lifts the blacks for a washed-out, matte look.</summary>
    private static void Fade(IMagickImage<byte> image, double amount)
    {
        image.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 1 - amount);
        image.Evaluate(Channels.RGB, EvaluateOperator.Add, Quantum.Max * amount);
    }

    /// <summary>Darkens the corners: 0 none, 100 strong.</summary>
    private static void ApplyVignette(IMagickImage<byte> image, int strength)
    {
        using var mask = new MagickImage("radial-gradient:white-black", image.Width, image.Height);
        mask.Negate();                                                     // 0 in the centre, 1 at the corners
        mask.Evaluate(Channels.All, EvaluateOperator.Pow, 2);              // keep the middle clean
        mask.Evaluate(Channels.All, EvaluateOperator.Multiply, strength / 100.0 * 0.8);
        mask.Negate();                                                     // 1 in the centre, darker at the corners
        image.Composite(mask, CompositeOperator.Multiply, Channels.RGB);
    }
}
