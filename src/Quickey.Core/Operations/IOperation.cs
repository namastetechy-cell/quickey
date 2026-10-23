namespace Quickey.Core.Operations;

public interface IOperation
{
    /// <summary>Stable identifier, e.g. "image.to.png". Used in settings and telemetry-free usage counting.</summary>
    string Id { get; }

    /// <summary>Short label shown on the wheel wedge, e.g. "PNG".</summary>
    string Label { get; }

    /// <summary>Segoe Fluent Icons glyph shown above the label.</summary>
    string Glyph { get; }

    OperationCategory Category { get; }

    /// <summary>Whether running this uses up one of the free tier's daily conversions.</summary>
    bool CountsTowardLimit { get; }

    bool AppliesTo(IReadOnlyList<string> files);

    /// <summary>
    /// Why this operation can't run on this PC right now (e.g. "Needs Microsoft Word"), or null if it can.
    /// The wheel still shows it, greyed out, so people learn the option exists.
    /// </summary>
    string? UnavailableReason => null;

    Task<OperationResult> RunAsync(IReadOnlyList<string> files, OperationContext context, CancellationToken cancellationToken);
}
