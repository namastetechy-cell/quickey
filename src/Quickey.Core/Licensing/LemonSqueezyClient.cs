using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Quickey.Core.Licensing;

/// <summary>Which Lemon Squeezy product a key must belong to. Keys from any other store or product are rejected.</summary>
public sealed record LicenseProduct(long StoreId, long ProductId);

public enum LicenseCheck
{
    Valid,
    Invalid,
    Unreachable,
}

public sealed record ActivationResult(bool Success, string? Error, LicenseRecord? Record);

/// <summary>
/// Thin client for the Lemon Squeezy License API (https://docs.lemonsqueezy.com/api/license-api).
/// These endpoints take the license key itself as the credential; no API key ships in the app.
/// </summary>
public sealed class LemonSqueezyClient(HttpClient http, LicenseProduct product, TimeProvider? clock = null)
{
    private static readonly Uri BaseUri = new("https://api.lemonsqueezy.com/v1/licenses/");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ActivationResult> ActivateAsync(string key, string instanceName, CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        LicenseResponse? response;
        try
        {
            response = await PostAsync("activate", new() { ["license_key"] = key, ["instance_name"] = instanceName }, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new(false, "Couldn't reach the license server. Check your internet connection.", null);
        }

        if (response is null || !response.Activated || response.Instance is null)
            return new(false, response?.Error ?? "That license key wasn't accepted.", null);

        if (!IsOurProduct(response))
        {
            // Activated against someone else's product: release the seat and refuse.
            await TryDeactivateAsync(key, response.Instance.Id, cancellationToken);
            return new(false, "That key isn't a Quickey license.", null);
        }

        var record = new LicenseRecord(key, response.Instance.Id, response.Meta?.CustomerEmail, _clock.GetUtcNow());
        return new(true, null, record);
    }

    public async Task<LicenseCheck> ValidateAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync("validate", new() { ["license_key"] = record.Key, ["instance_id"] = record.InstanceId }, cancellationToken);
            return response is { Valid: true } && IsOurProduct(response) && response.LicenseKey?.Status is not ("disabled" or "expired")
                ? LicenseCheck.Valid
                : LicenseCheck.Invalid;
        }
        catch (HttpRequestException)
        {
            return LicenseCheck.Unreachable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LicenseCheck.Unreachable;
        }
    }

    public async Task<bool> TryDeactivateAsync(string key, string instanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync("deactivate", new() { ["license_key"] = key, ["instance_id"] = instanceId }, cancellationToken);
            return response?.Deactivated == true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private bool IsOurProduct(LicenseResponse response) =>
        response.Meta is { } meta && meta.StoreId == product.StoreId && meta.ProductId == product.ProductId;

    private async Task<LicenseResponse?> PostAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, endpoint)) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        // 4xx responses still carry a JSON body explaining why (e.g. "license_key not found").
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"License server returned {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<LicenseResponse>(cancellationToken);
    }

    internal sealed class LicenseResponse
    {
        [JsonPropertyName("activated")] public bool Activated { get; set; }
        [JsonPropertyName("valid")] public bool Valid { get; set; }
        [JsonPropertyName("deactivated")] public bool Deactivated { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("license_key")] public KeyInfo? LicenseKey { get; set; }
        [JsonPropertyName("instance")] public InstanceInfo? Instance { get; set; }
        [JsonPropertyName("meta")] public MetaInfo? Meta { get; set; }
    }

    internal sealed class KeyInfo
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    internal sealed class InstanceInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
    }

    internal sealed class MetaInfo
    {
        [JsonPropertyName("store_id")] public long StoreId { get; set; }
        [JsonPropertyName("product_id")] public long ProductId { get; set; }
        [JsonPropertyName("customer_email")] public string? CustomerEmail { get; set; }
    }
}
