namespace Quickey.Core.Operations;

public sealed record OperationError(string File, string Message);

public sealed class OperationResult(string label)
{
    public string Label { get; } = label;
    public List<string> Outputs { get; } = [];
    public List<OperationError> Errors { get; } = [];
    public bool Succeeded => Errors.Count == 0;

    public static OperationResult Failed(string label, string file, string message)
    {
        var result = new OperationResult(label);
        result.Errors.Add(new OperationError(file, message));
        return result;
    }
}
