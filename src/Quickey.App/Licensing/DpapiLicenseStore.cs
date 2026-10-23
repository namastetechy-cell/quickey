using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Quickey.Core.Licensing;

namespace Quickey.App.Licensing;

/// <summary>Stores the activation encrypted to the current Windows user, so the file is useless on another PC.</summary>
public sealed class DpapiLicenseStore(string path) : ILicenseStore
{
    private static readonly byte[] Entropy = "Quickey.License.v1"u8.ToArray();

    public LicenseRecord? Load()
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LicenseRecord>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public void Save(LicenseRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        File.WriteAllBytes(path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
