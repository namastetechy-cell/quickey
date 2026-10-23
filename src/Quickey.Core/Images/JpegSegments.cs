namespace Quickey.Core.Images;

/// <summary>
/// Rewrites the metadata segments of a JPEG without touching the compressed image data, so editing
/// or removing EXIF never costs quality. Everything from the start-of-scan marker on is copied byte for byte.
/// </summary>
public static class JpegSegments
{
    private const byte App0 = 0xE0, App1 = 0xE1, App13 = 0xED, Comment = 0xFE, StartOfScan = 0xDA;
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    public static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

    /// <param name="exif">New EXIF APP1 payload starting with "Exif\0\0", or null for none.</param>
    /// <param name="stripOther">Also drop XMP, IPTC/Photoshop and comment segments. Colour profiles are kept.</param>
    public static byte[] Rewrite(byte[] jpeg, byte[]? exif, bool stripOther)
    {
        if (!IsJpeg(jpeg))
            throw new InvalidDataException("Not a JPEG file.");
        if (exif is not null && exif.Length + 2 > ushort.MaxValue)
            throw new InvalidDataException("The metadata is too large to fit in a JPEG.");

        using var output = new MemoryStream(jpeg.Length + (exif?.Length ?? 0));
        output.Write(jpeg, 0, 2);
        var exifWritten = exif is null;
        var position = 2;

        while (position < jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
                throw new InvalidDataException("Damaged JPEG: expected a marker.");
            var marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }
            if (marker == StartOfScan)
            {
                WriteExifOnce();
                output.Write(jpeg, position, jpeg.Length - position);
                break;
            }

            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            var payload = new ReadOnlySpan<byte>(jpeg, position + 4, Math.Max(0, length - 2));
            // EXIF belongs right after the JFIF header, before any other segment.
            if (marker != App0)
                WriteExifOnce();

            var isExif = marker == App1 && payload.StartsWith(ExifHeader);
            var isXmp = marker == App1 && payload.StartsWith(XmpHeader);
            var drop = isExif || (stripOther && (isXmp || marker == App13 || marker == Comment));
            if (!drop)
                output.Write(jpeg, position, length + 2);
            position += length + 2;
        }
        return output.ToArray();

        void WriteExifOnce()
        {
            if (exifWritten)
                return;
            exifWritten = true;
            var length = exif!.Length + 2;
            output.Write([0xFF, App1, (byte)(length >> 8), (byte)length]);
            output.Write(exif);
        }
    }
}
