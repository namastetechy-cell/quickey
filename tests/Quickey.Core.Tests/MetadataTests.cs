using ImageMagick;
using Quickey.Core.Images;
using Quickey.Core.Operations;

namespace Quickey.Core.Tests;

public class MetadataTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Jpeg(string name = "photo.jpg", ushort orientation = 1)
    {
        var path = _temp.File(name);
        using var image = new MagickImage("plasma:", 320, 240) { Quality = 90 };
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Nikon");
        exif.SetValue(ExifTag.Model, "Z6");
        exif.SetValue(ExifTag.DateTimeOriginal, "2023:07:14 18:05:00");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(48), new Rational(51), new Rational(30)]);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(2), new Rational(17), new Rational(40)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        image.SetProfile(exif);
        image.Write(path);
        if (orientation != 1)
        {
            // Magick writes its own orientation on save; patch the tag in losslessly instead.
            using var info = new MagickImage();
            info.Ping(path);
            var profile = info.GetExifProfile()!;
            profile.SetValue(ExifTag.Orientation, orientation);
            File.WriteAllBytes(path, JpegSegments.Rewrite(File.ReadAllBytes(path), profile.ToByteArray(), stripOther: false));
        }
        return path;
    }

    /// <summary>Everything from the start-of-scan marker: the compressed pixels.</summary>
    private static byte[] ScanData(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 2; i < bytes.Length - 1; i++)
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA)
                return bytes[i..];
        throw new InvalidDataException("no SOS");
    }

    [Fact]
    public void Reads_camera_date_and_location()
    {
        var metadata = MetadataEditor.Read(Jpeg());

        Assert.Equal(("Nikon", "Z6", "2023-07-14 18:05:00"), (metadata.Make, metadata.Model, metadata.DateTaken));
        Assert.Equal(48.858333, metadata.Latitude!.Value, 5);
        Assert.Equal(2.294444, metadata.Longitude!.Value, 5);
    }

    [Fact]
    public void Editing_a_jpeg_never_touches_the_image_data()
    {
        var source = Jpeg();
        var output = _temp.File("out.jpg");

        MetadataEditor.Write(source, output, new MetadataChange { Artist = "Asha", Make = "", Location = "-33.8568, 151.2153", DateTaken = "2024-01-02 03:04" });

        Assert.Equal(ScanData(source), ScanData(output));
        var edited = MetadataEditor.Read(output);
        Assert.Equal("Asha", edited.Artist);
        Assert.Null(edited.Make);
        Assert.Equal("Z6", edited.Model); // untouched field kept
        Assert.Equal("2024-01-02 03:04:00", edited.DateTaken);
        Assert.Equal(-33.8568, edited.Latitude!.Value, 4);
        Assert.Equal(151.2153, edited.Longitude!.Value, 4);
    }

    [Fact]
    public void Clearing_location_removes_all_gps_tags()
    {
        var source = Jpeg();

        MetadataEditor.Write(source, source, new MetadataChange { Location = "" });

        var after = MetadataEditor.Read(source);
        Assert.Null(after.Location);
        Assert.Equal("Nikon", after.Make);
    }

    [Fact]
    public void Remove_all_keeps_only_the_rotation_and_the_pixels()
    {
        var source = Jpeg(orientation: 6);
        var output = _temp.File("clean.jpg");

        MetadataEditor.Write(source, output, new MetadataChange { RemoveAll = true });

        Assert.Equal(ScanData(source), ScanData(output));
        using var image = new MagickImage();
        image.Ping(output);
        var exif = image.GetExifProfile();
        Assert.Equal((ushort)6, exif?.GetValue(ExifTag.Orientation)?.Value);
        Assert.Null(exif?.GetValue(ExifTag.Make));
        Assert.Null(MetadataEditor.Read(output).Location);
    }

    [Fact]
    public void Writes_png_metadata_by_reencoding()
    {
        var png = _temp.Image("shot.png");

        MetadataEditor.Write(png, png, new MetadataChange { Description = "Quarterly chart", Copyright = "© Me" });

        var metadata = MetadataEditor.Read(png);
        Assert.Equal(("Quarterly chart", "© Me"), (metadata.Description, metadata.Copyright));
        Assert.Equal(MagickFormat.Png, new MagickImageInfo(png).Format);
    }

    [Theory]
    [InlineData("12.9716, 77.5946", 12.9716, 77.5946)]
    [InlineData("-33.86 151.21", -33.86, 151.21)]
    public void Parses_locations(string text, double lat, double lon) =>
        Assert.Equal((lat, lon), MetadataEditor.ParseLocation(text));

    [Theory]
    [InlineData("north pole")]
    [InlineData("95, 10")]
    public void Rejects_bad_locations(string text) =>
        Assert.Throws<FormatException>(() => MetadataEditor.ParseLocation(text));

    [Fact]
    public void Dates_are_normalised_or_rejected()
    {
        Assert.Equal("2024:05:01 14:30:00", MetadataEditor.ToExifDate("2024-05-01 14:30"));
        Assert.Throws<FormatException>(() => MetadataEditor.ToExifDate("someday"));
    }

    [Fact]
    public void Rewrite_rejects_non_jpegs() =>
        Assert.Throws<InvalidDataException>(() => JpegSegments.Rewrite([1, 2, 3, 4], null, false));

    [Fact]
    public async Task Operation_saves_a_copy_unless_overwrite_is_chosen()
    {
        var source = Jpeg();
        var dialogs = new FakeDialogs { Metadata = new MetadataChange { Artist = "Ravi" } };
        var registry = OperationRegistry.CreateDefault(FakeDialogs.With(fake: dialogs));

        var copy = await registry.Find("image.metadata")!.RunAsync([source], new OperationContext(), CancellationToken.None);
        Assert.Equal(_temp.File("photo-edited.jpg"), Assert.Single(copy.Outputs));
        Assert.Null(MetadataEditor.Read(source).Artist);

        dialogs.Metadata = new MetadataChange { Artist = "Ravi", OverwriteOriginal = true };
        var inPlace = await registry.Find("image.metadata")!.RunAsync([source], new OperationContext(), CancellationToken.None);
        Assert.Equal(source, Assert.Single(inPlace.Outputs));
        Assert.Equal("Ravi", MetadataEditor.Read(source).Artist);
    }
}
