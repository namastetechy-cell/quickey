using Quickey.Core.Operations;

namespace Quickey.Core.Images;

public interface ICompressPrompt
{
    /// <summary>Returns the chosen options, or null if the user cancelled.</summary>
    Task<CompressOptions?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken);
}

public interface IMetadataPrompt
{
    Task<MetadataChange?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken);
}

public interface IPhotoEditPrompt
{
    Task<PhotoAdjustments?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken);
}

/// <summary>The windows the Windows app provides for the image tools that need choices.</summary>
public sealed record ImageDialogs(ICropPrompt Crop, ICompressPrompt Compress, IMetadataPrompt Metadata, IPhotoEditPrompt Edit);

/// <summary>Base for tools that ask once, then process each image with the answer.</summary>
public abstract class DialogImageOperation<TChoice> : IOperation where TChoice : class
{
    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string Glyph { get; }
    public OperationCategory Category => OperationCategory.Tools;
    public bool CountsTowardLimit => true;

    public bool AppliesTo(IReadOnlyList<string> files) => FileKinds.All(files, FileKind.Image);

    protected abstract Task<TChoice?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken);

    protected abstract string Process(string file, TChoice choice, OperationContext context, OperationResult result);

    public async Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken)
    {
        var result = new OperationResult(Label);
        var choice = await AskAsync(files, cancellationToken);
        if (choice is null)
        {
            result.Skipped = files.Count;
            return result;
        }
        foreach (var file in files)
        {
            try
            {
                result.Outputs.Add(await Task.Run(() => Process(file, choice, context, result), cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add(new OperationError(file, ex.Message));
            }
        }
        Finish(result);
        return result;
    }

    protected virtual void Finish(OperationResult result)
    {
    }
}

public sealed class CompressImagesOperation(ICompressPrompt prompt) : DialogImageOperation<CompressOptions>
{
    private long _before;
    private long _after;
    private int _missed;

    public override string Id => "image.compress";
    public override string Label => "Compress";
    public override string Glyph => Glyphs.Compress;

    protected override Task<CompressOptions?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        _before = _after = _missed = 0;
        return prompt.AskAsync(files, cancellationToken);
    }

    protected override string Process(string file, CompressOptions options, OperationContext context, OperationResult result)
    {
        var compressed = ImageCompressor.Compress(file, options);
        var sameFormat = compressed.Target.Matches(file);
        if (options.Mode == CompressMode.Preset && sameFormat && options.MaxSide is null && compressed.Data.Length >= compressed.OriginalBytes)
            throw new InvalidOperationException("Already as small as it gets at this setting.");

        var output = context.OutputPathFor(file, compressed.Target.Extension, "-compressed");
        File.WriteAllBytes(output, compressed.Data);
        Interlocked.Add(ref _before, compressed.OriginalBytes);
        Interlocked.Add(ref _after, compressed.Data.Length);
        if (!compressed.MetTarget)
            Interlocked.Increment(ref _missed);
        return output;
    }

    protected override void Finish(OperationResult result)
    {
        if (result.Outputs.Count == 0)
            return;
        result.Note = $"{ImageCompressor.FormatBytes(_before)} → {ImageCompressor.FormatBytes(_after)}"
            + (_missed > 0 ? $". {_missed} couldn't reach the target size and were made as small as possible." : "");
    }
}

public sealed class EditMetadataOperation(IMetadataPrompt prompt) : DialogImageOperation<MetadataChange>
{
    public override string Id => "image.metadata";
    public override string Label => "Metadata";
    public override string Glyph => Glyphs.Tag;

    protected override Task<MetadataChange?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        prompt.AskAsync(files, cancellationToken);

    protected override string Process(string file, MetadataChange change, OperationContext context, OperationResult result)
    {
        var output = change.OverwriteOriginal ? file : context.OutputPathFor(file, Path.GetExtension(file), "-edited");
        MetadataEditor.Write(file, output, change);
        return output;
    }
}

public sealed class EditPhotoOperation(IPhotoEditPrompt prompt) : DialogImageOperation<PhotoAdjustments>
{
    public override string Id => "image.edit";
    public override string Label => "Edit";
    public override string Glyph => Glyphs.Sliders;

    protected override Task<PhotoAdjustments?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        prompt.AskAsync(files, cancellationToken);

    protected override string Process(string file, PhotoAdjustments adjustments, OperationContext context, OperationResult result) =>
        PhotoEditor.Save(file, adjustments, context);
}
