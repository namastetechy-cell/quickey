using System.Text.Json;

namespace Quickey.Core.Licensing;

/// <summary>
/// Counts the free tier's conversions per local calendar day. Each input file counts once,
/// so converting 3 photos uses 3 of the allowance. File actions never count.
/// </summary>
public sealed class UsageTracker(string path, TimeProvider? clock = null)
{
    public const int FreeDailyConversions = 10;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();

    private sealed record Usage(DateOnly Day, int Count);

    public int UsedToday
    {
        get
        {
            lock (_gate)
                return Load().Count;
        }
    }

    public int RemainingToday => Math.Max(0, FreeDailyConversions - UsedToday);

    public bool CanConsume(int files) => files <= RemainingToday;

    /// <summary>Records <paramref name="files"/> conversions. Returns false (and records nothing) if that would exceed today's allowance.</summary>
    public bool TryConsume(int files)
    {
        lock (_gate)
        {
            var usage = Load();
            if (usage.Count + files > FreeDailyConversions)
                return false;
            Save(usage with { Count = usage.Count + files });
            return true;
        }
    }

    /// <summary>Records conversions that already happened, capped at today's allowance.</summary>
    public void Record(int files)
    {
        lock (_gate)
        {
            var usage = Load();
            Save(usage with { Count = Math.Min(FreeDailyConversions, usage.Count + Math.Max(0, files)) });
        }
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private Usage Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var usage = JsonSerializer.Deserialize<Usage>(File.ReadAllText(path));
                if (usage is not null && usage.Day == Today)
                    return usage;
            }
        }
        catch (JsonException)
        {
        }
        return new Usage(Today, 0);
    }

    private void Save(Usage usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(usage));
    }
}
