using Quickey.Core.Operations;

namespace Quickey.Core.Licensing;

/// <summary>The single place that answers "may this run right now?".</summary>
public sealed class Entitlements(LicenseManager license, UsageTracker usage)
{
    public bool IsPro => license.IsPro;

    public int RemainingToday => IsPro ? int.MaxValue : usage.RemainingToday;

    public bool CanRun(IOperation operation, int fileCount) =>
        IsPro || !operation.CountsTowardLimit || usage.CanConsume(fileCount);

    /// <summary>Charges the free allowance for a run that is about to start. Returns false if it doesn't fit.</summary>
    public bool TryCharge(IOperation operation, int fileCount) =>
        IsPro || !operation.CountsTowardLimit || usage.TryConsume(fileCount);
}
