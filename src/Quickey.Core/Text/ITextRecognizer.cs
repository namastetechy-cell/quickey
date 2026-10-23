namespace Quickey.Core.Text;

/// <summary>
/// OCR. The Windows app supplies the OCR engine built into Windows; elsewhere there is none and
/// scanned PDFs report that they have no text layer.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>Reads the text in an encoded image (PNG). Returns an empty string if nothing is legible.</summary>
    Task<string> RecognizeAsync(byte[] image, CancellationToken cancellationToken);
}
