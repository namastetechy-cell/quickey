using Quickey.Core.Images;

namespace Quickey.Core.Tests;

/// <summary>Stand-ins for the app's windows: each returns a fixed answer and records what it was asked.</summary>
public sealed class FakeDialogs : ICompressPrompt, IMetadataPrompt, IPhotoEditPrompt, ICropPrompt
{
    public CompressOptions? Compress { get; set; } = new();
    public MetadataChange? Metadata { get; set; } = new();
    public PhotoAdjustments? Edit { get; set; } = new();
    public List<IReadOnlyList<string>> Asked { get; } = [];

    public static ImageDialogs With(ICropPrompt? crop = null, FakeDialogs? fake = null)
    {
        fake ??= new FakeDialogs();
        return new ImageDialogs(crop ?? fake, fake, fake, fake);
    }

    Task<CompressOptions?> ICompressPrompt.AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) => Answer(files, Compress);
    Task<MetadataChange?> IMetadataPrompt.AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) => Answer(files, Metadata);
    Task<PhotoAdjustments?> IPhotoEditPrompt.AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) => Answer(files, Edit);
    Task<CropDecision> ICropPrompt.AskAsync(string file, int index, int count, CancellationToken cancellationToken) =>
        Task.FromResult(new CropDecision(CropChoice.Skip));

    private Task<T?> Answer<T>(IReadOnlyList<string> files, T? answer) where T : class
    {
        Asked.Add(files);
        return Task.FromResult(answer);
    }
}
