namespace Quickey.Core.Licensing;

/// <summary>
/// Decides whether this PC is Pro. A license is re-checked online at most once a day; if the server
/// can't be reached the license keeps working for <see cref="OfflineGrace"/> after the last good check.
/// </summary>
public sealed class LicenseManager(LemonSqueezyClient client, ILicenseStore store, TimeProvider? clock = null)
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private LicenseRecord? _record = store.Load();

    public event EventHandler? Changed;

    public LicenseRecord? Current => _record;

    public bool IsPro => _record is not null && _clock.GetUtcNow() - _record.LastValidated <= OfflineGrace;

    public async Task<ActivationResult> ActivateAsync(string key, string instanceName, CancellationToken cancellationToken = default)
    {
        var result = await client.ActivateAsync(key, instanceName, cancellationToken);
        if (result is { Success: true, Record: { } record })
        {
            store.Save(record);
            _record = record;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    /// <summary>Frees this PC's seat so the key can be used on another one.</summary>
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        if (_record is null)
            return;
        await client.TryDeactivateAsync(_record.Key, _record.InstanceId, cancellationToken);
        Forget();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_record is null || _clock.GetUtcNow() - _record.LastValidated < RecheckInterval)
            return;

        switch (await client.ValidateAsync(_record, cancellationToken))
        {
            case LicenseCheck.Valid:
                _record = _record with { LastValidated = _clock.GetUtcNow() };
                store.Save(_record);
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case LicenseCheck.Invalid:
                Forget();
                break;
            case LicenseCheck.Unreachable:
                // Keep working offline until the grace period runs out.
                break;
        }
    }

    private void Forget()
    {
        store.Clear();
        _record = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
