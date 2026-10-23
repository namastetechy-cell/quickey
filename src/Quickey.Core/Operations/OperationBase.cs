namespace Quickey.Core.Operations;

/// <summary>
/// Base for operations that process each input file on its own. Failures on one file
/// are recorded and do not stop the rest of the batch.
/// </summary>
public abstract class PerFileOperation : IOperation
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string Glyph { get; }
    public abstract OperationCategory Category { get; }
    public virtual bool CountsTowardLimit => true;

    public abstract bool AppliesTo(IReadOnlyList<string> files);

    public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
    {
        var result = new OperationResult(Label);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outputs = await Task.Run(() => ProcessFile(file, context, cancellationToken), cancellationToken);
                result.Outputs.AddRange(outputs);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add(new OperationError(file, ex.Message));
            }
        }
        return result;
    }

    protected abstract IEnumerable<string> ProcessFile(string file, OperationContext context, CancellationToken cancellationToken);
}
