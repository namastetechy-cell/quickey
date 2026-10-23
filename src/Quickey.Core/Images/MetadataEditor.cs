using System.Globalization;
using ImageMagick;

namespace Quickey.Core.Images;

/// <summary>The editable tags of one photo. Dates use "yyyy-MM-dd HH:mm:ss"; location is decimal degrees.</summary>
public sealed record PhotoMetadata(
    string? Make, string? Model, string? DateTaken, string? Artist, string? Copyright, string? Description,
    double? Latitude, double? Longitude)
{
    public string? Location => Latitude is { } lat && Longitude is { } lon
        ? string.Create(CultureInfo.InvariantCulture, $"{lat:0.######}, {lon:0.######}")
        : null;
}

/// <summary>
/// What to change. For each text field: null keeps the photo's own value, "" removes it, anything
/// else sets it. Location is "latitude, longitude" in decimal degrees.
/// </summary>
public sealed record MetadataChange
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? DateTaken { get; init; }
    public string? Artist { get; init; }
    public string? Copyright { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }

    /// <summary>Strip everything (EXIF, XMP, IPTC, comments), keeping only the rotation so the photo still displays upright.</summary>
    public bool RemoveAll { get; init; }

    /// <summary>Write back to the original file instead of saving "photo-edited.jpg".</summary>
    public bool OverwriteOriginal { get; init; }
}

public static class MetadataEditor
{
    private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";
    private const string DisplayDateFormat = "yyyy-MM-dd HH:mm:ss";

    public static PhotoMetadata Read(string path)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var exif = image.GetExifProfile();
        if (exif is null)
            return new PhotoMetadata(null, null, null, null, null, null, null, null);

        string? Text(ExifTag<string> tag) => exif.GetValue(tag)?.Value is { Length: > 0 } value ? value.Trim('\0', ' ') : null;

        var date = Text(ExifTag.DateTimeOriginal) ?? Text(ExifTag.DateTime);
        if (date is not null && DateTime.TryParseExact(date, ExifDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = parsed.ToString(DisplayDateFormat, CultureInfo.InvariantCulture);

        return new PhotoMetadata(
            Text(ExifTag.Make), Text(ExifTag.Model), date, Text(ExifTag.Artist), Text(ExifTag.Copyright), Text(ExifTag.ImageDescription),
            Degrees(exif.GetValue(ExifTag.GPSLatitude)?.Value, Text(ExifTag.GPSLatitudeRef), "S"),
            Degrees(exif.GetValue(ExifTag.GPSLongitude)?.Value, Text(ExifTag.GPSLongitudeRef), "W"));
    }

    /// <summary>Applies <paramref name="change"/> and writes the result to <paramref name="output"/> (which may be the source).</summary>
    public static void Write(string source, string output, MetadataChange change)
    {
        var bytes = File.ReadAllBytes(source);
        byte[] result;
        if (JpegSegments.IsJpeg(bytes))
        {
            // Lossless: only the metadata segments are rewritten.
            using var info = new MagickImage();
            info.Ping(bytes);
            var exif = change.RemoveAll ? OrientationOnly(info.GetExifProfile()) : Apply(info.GetExifProfile() ?? new ExifProfile(), change);
            result = JpegSegments.Rewrite(bytes, exif?.Values.Any() == true ? exif.ToByteArray() : null, change.RemoveAll);
        }
        else
        {
            // Other formats are re-encoded (PNG stays lossless; lossy formats at high quality).
            var target = TransformImageOperation.SameFormatTarget(source);
            using var frames = ImageCodec.ReadFrames(source);
            foreach (var frame in frames)
            {
                if (change.RemoveAll)
                    frame.Strip();
                else
                    frame.SetProfile(Apply(frame.GetExifProfile() ?? new ExifProfile(), change));
            }
            using var stream = new MemoryStream();
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".quickey-{Guid.NewGuid():N}{target.Extension}");
            try
            {
                ImageCodec.Write(frames, temp, target, quality: 92);
                result = File.ReadAllBytes(temp);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        // Write next to the destination first, then swap, so a failure never leaves a half-written original.
        var staging = output + ".quickey-tmp";
        File.WriteAllBytes(staging, result);
        File.Move(staging, output, overwrite: true);
    }

    private static IExifProfile? OrientationOnly(IExifProfile? exif)
    {
        var orientation = exif?.GetValue(ExifTag.Orientation)?.Value;
        if (orientation is null or 0 or 1)
            return null;
        var kept = new ExifProfile();
        kept.SetValue(ExifTag.Orientation, orientation.Value);
        return kept;
    }

    private static IExifProfile Apply(IExifProfile exif, MetadataChange change)
    {
        SetText(exif, ExifTag.Make, change.Make);
        SetText(exif, ExifTag.Model, change.Model);
        SetText(exif, ExifTag.Artist, change.Artist);
        SetText(exif, ExifTag.Copyright, change.Copyright);
        SetText(exif, ExifTag.ImageDescription, change.Description);

        if (change.DateTaken is { } date)
        {
            var exifDate = date.Length == 0 ? "" : ToExifDate(date);
            SetText(exif, ExifTag.DateTimeOriginal, exifDate);
            SetText(exif, ExifTag.DateTimeDigitized, exifDate);
        }

        if (change.Location is { } location)
        {
            foreach (var tag in new ExifTag[] { ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef, ExifTag.GPSAltitude, ExifTag.GPSAltitudeRef, ExifTag.GPSTimestamp, ExifTag.GPSDateStamp })
                exif.RemoveValue(tag);
            if (location.Length > 0)
            {
                var (lat, lon) = ParseLocation(location);
                exif.SetValue(ExifTag.GPSLatitude, ToDms(lat));
                exif.SetValue(ExifTag.GPSLatitudeRef, lat < 0 ? "S" : "N");
                exif.SetValue(ExifTag.GPSLongitude, ToDms(lon));
                exif.SetValue(ExifTag.GPSLongitudeRef, lon < 0 ? "W" : "E");
            }
        }
        return exif;
    }

    private static void SetText(IExifProfile exif, ExifTag<string> tag, string? value)
    {
        if (value is null)
            return;
        if (value.Length == 0)
            exif.RemoveValue(tag);
        else
            exif.SetValue(tag, value);
    }

    /// <summary>Accepts "2024-05-01 14:30", "2024-05-01", "1 May 2024 2:30 PM"... Throws FormatException otherwise.</summary>
    public static string ToExifDate(string text)
    {
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
            return date.ToString(ExifDateFormat, CultureInfo.InvariantCulture);
        throw new FormatException($"\"{text}\" isn't a date. Try 2024-05-01 14:30.");
    }

    /// <summary>"12.9716, 77.5946" → (12.9716, 77.5946). Throws FormatException for anything else or out-of-range values.</summary>
    public static (double Latitude, double Longitude) ParseLocation(string text)
    {
        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
            return (lat, lon);
        throw new FormatException($"\"{text}\" isn't a location. Use latitude, longitude, e.g. 12.9716, 77.5946.");
    }

    private static Rational[] ToDms(double degrees)
    {
        degrees = Math.Abs(degrees);
        var d = Math.Floor(degrees);
        var minutes = (degrees - d) * 60;
        var m = Math.Floor(minutes);
        var s = (minutes - m) * 60;
        return [new Rational((uint)d, 1), new Rational((uint)m, 1), new Rational((uint)Math.Round(s * 10000), 10000)];
    }

    private static double? Degrees(Rational[]? dms, string? reference, string negative)
    {
        if (dms is not { Length: 3 } || dms.Any(r => r.Denominator == 0))
            return null;
        var value = dms[0].ToDouble() + dms[1].ToDouble() / 60 + dms[2].ToDouble() / 3600;
        return reference == negative ? -value : value;
    }
}
