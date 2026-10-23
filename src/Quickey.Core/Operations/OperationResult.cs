namespace Quickey.Core.Operations;

public sealed record OperationError(string File, string Message);

public sealed class OperationResult(string label)
{
    public string Label { get; } = label;
    public List<string> Outputs { get; } = [];
    public List<OperationError> Errors { get; } = [];

    /// <summary>Files the user chose to skip (e.g. in the crop window). Not errors, and not charged.</summary>
    public int Skipped { get; set; }
    public bool Succeeded => Errors.Count == 0;

    public static OperationResult Failed(string label, string file, string message)
    {
        var result = new OperationResult(label);
        result.Errors.Add(new OperationError(file, message));
        return result;
    }
}
