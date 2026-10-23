using System.Runtime.InteropServices.WindowsRuntime;
using Quickey.Core.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Quickey.App.Text;

/// <summary>
/// The OCR engine built into Windows 10/11, in the languages installed for the user. Offline and
/// free. Reads printed text well; handwriting much less so.
/// </summary>
public sealed class WindowsOcr : ITextRecognizer
{
    private readonly Lazy<OcrEngine?> _engine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken)
    {
        var engine = _engine.Value ?? throw new InvalidOperationException(
            "Windows text recognition isn't available. Add a language with \"Optical character recognition\" in Settings → Time & language → Language.");

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException("The page is too large for text recognition.");

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }
}
