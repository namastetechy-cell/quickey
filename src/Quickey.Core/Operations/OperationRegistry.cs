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
    /// Every operation that runs on any platform: images and PDF. The image tools that need choices
    /// (crop, compress, metadata, edit) come from the app's windows; without them, quick one-click
    /// versions are offered instead. Scanned PDFs need OCR to give text.
    /// </summary>
    public static OperationRegistry CreateDefault(Images.ImageDialogs? dialogs = null, Text.ITextRecognizer? ocr = null, Documents.SheetChooser? sheets = null)
    {
        var registry = new OperationRegistry();
        if (dialogs is not null)
        {
            // Registration order is wheel order: the most-used tools come first.
            registry.Add(new Images.CompressImagesOperation(dialogs.Compress));
            registry.Add(new Images.CropImageOperation(dialogs.Crop));
            registry.Add(new Images.EditPhotoOperation(dialogs.Edit));
            registry.Add(new Images.EditMetadataOperation(dialogs.Metadata));
        }
        return registry
            .AddRange(Images.ImageOperations.All(quickTools: dialogs is null))
            .AddRange(Pdf.PdfOperations.All(ocr))
            .AddRange(Documents.SpreadsheetOperations.All(sheets));
    }
}
