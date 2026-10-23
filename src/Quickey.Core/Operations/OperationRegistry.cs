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

    /// <summary>Every operation that runs on any platform: images and PDF.</summary>
    public static OperationRegistry CreateDefault() =>
        new OperationRegistry()
            .AddRange(Images.ImageOperations.All())
            .AddRange(Pdf.PdfOperations.All());
}
