using System.Runtime.InteropServices;

namespace Quickey.App.Office;

/// <summary>One of the Office apps Quickey drives through COM. Detection only reads the registry; nothing is launched.</summary>
internal sealed class OfficeApp(string progId, string displayName, object alertsOff)
{
    public static readonly OfficeApp Word = new("Word.Application", "Microsoft Word", 0 /* wdAlertsNone */);
    public static readonly OfficeApp Excel = new("Excel.Application", "Microsoft Excel", false);
    public static readonly OfficeApp PowerPoint = new("PowerPoint.Application", "Microsoft PowerPoint", 1 /* ppAlertsNone */);

    private readonly Lazy<bool> _installed = new(() => Type.GetTypeFromProgID(progId) is not null);

    // One conversion per app at a time: Office automation is not safe to drive concurrently.
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool IsInstalled => _installed.Value;

    public string? UnavailableReason => IsInstalled ? null : "Needs " + displayName;

    /// <summary>Starts a hidden instance, runs <paramref name="work"/>, and always quits it.</summary>
    public async Task Use(Action<object> work, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await StaWorker.Run<object?>(() =>
            {
                var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException(UnavailableReason);
                var app = Activator.CreateInstance(type)!;
                try
                {
                    Dispatch.Set(app, "DisplayAlerts", alertsOff);
                    work(app);
                }
                finally
                {
                    try { Dispatch.Call(app, "Quit"); } catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException) { }
                    Marshal.FinalReleaseComObject(app);
                }
                return null;
            }, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
