namespace Quickey.Core.Operations;

public sealed class OperationRegistry
{
    private readonly List<IOperation> _operations = [];

    public IReadOnlyList<IOperation> All => _operations;

    public OperationRegistry Add(IOperation operation)
    {
        if (_operations.Any(o => o.Id == operation.Id))
            throw new InvalidOperationException($"Duplicate operation id '{operation.Id}'.");
        _operations.Add(operation);
        return this;
    }

    public OperationRegistry AddRange(IEnumerable<IOperation> operations)
    {
        foreach (var operation in operations)
            Add(operation);
        return this;
    }

    /// <summary>Operations of one wheel that can run on the given selection, in registration order.</summary>
    public IReadOnlyList<IOperation> For(OperationCategory category, IReadOnlyList<string> files) =>
        _operations.Where(o => o.Category == category && o.AppliesTo(files)).ToList();

    public IOperation? Find(string id) => _operations.FirstOrDefault(o => o.Id == id);

    /// <summary>
    /// Every operation that runs on any platform: images and PDF. Crop needs a window to ask where
    /// to crop, and scanned PDFs need OCR; both are left out or reduced when not supplied.
    /// </summary>
    public static OperationRegistry CreateDefault(Images.ICropPrompt? crop = null, Text.ITextRecognizer? ocr = null)
    {
        var registry = new OperationRegistry().AddRange(Images.ImageOperations.All());
        if (crop is not null)
            registry.Add(new Images.CropImageOperation(crop));
        return registry.AddRange(Pdf.PdfOperations.All(ocr));
    }
}
