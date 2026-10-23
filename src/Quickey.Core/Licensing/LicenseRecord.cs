namespace Quickey.Core.Licensing;

/// <summary>An activated license on this PC.</summary>
public sealed record LicenseRecord(string Key, string InstanceId, string? CustomerEmail, DateTimeOffset LastValidated);

/// <summary>Persists the activation. The Windows app encrypts it with DPAPI so it can't be copied to another PC.</summary>
public interface ILicenseStore
{
    LicenseRecord? Load();
    void Save(LicenseRecord record);
    void Clear();
}
