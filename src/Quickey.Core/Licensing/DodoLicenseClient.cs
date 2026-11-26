using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Quickey.Core.Licensing;

/// <summary>
/// Which Dodo Payments environment to ask, and which products' keys count as Quickey licenses
/// (keys for any other product are rejected).
/// </summary>
public sealed record LicenseServer(Uri BaseUri, IReadOnlyList<string> ProductIds)
{
    public static readonly Uri Live = new("https://live.dodopayments.com/");
    public static readonly Uri Test = new("https://test.dodopayments.com/");
}

public enum LicenseCheck
{
    Valid,
    Invalid,
    Unreachable,
}

public sealed record ActivationResult(bool Success, string? Error, LicenseRecord? Record);

/// <summary>
/// Thin client for Dodo Payments' public license endpoints (https://docs.dodopayments.com/features/license-keys).
/// They take the license key itself as the credential; no API key ships in the app.
/// </summary>
public sealed class DodoLicenseClient(HttpClient http, LicenseServer server, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ActivationResult> ActivateAsync(string key, string instanceName, CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        if (key.Length == 0)
            return new(false, "Paste your license key first.", null);

        ActivateResponse? activation;
        try
        {
            using var response = await PostAsync("licenses/activate", new { license_key = key, name = instanceName }, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new(false, "That license key wasn't found. Check that you copied all of it.", null);
                case HttpStatusCode.Forbidden:
                    return new(false, "That license key isn't active. It may have been refunded or disabled.", null);
                case HttpStatusCode.UnprocessableEntity:
                    return new(false, "This key is already active on 2 PCs. In Quickey's Settings on one of them, click Deactivate, then try again.", null);
            }
            if (!response.IsSuccessStatusCode)
                return new(false, "The license server had a problem. Please try again in a few minutes.", null);
            activation = await response.Content.ReadFromJsonAsync<ActivateResponse>(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, "Couldn't reach the license server. Check your internet connection.", null);
        }

        if (activation is null || activation.Id.Length == 0)
            return new(false, "That license key wasn't accepted.", null);

        if (activation.Product?.ProductId is not { } productId || !server.ProductIds.Contains(productId))
        {
            // Activated against someone else's product: release the seat and refuse.
            await TryDeactivateAsync(key, activation.Id, cancellationToken);
            return new(false, "That key isn't a Quickey license.", null);
        }

        return new(true, null, new LicenseRecord(key, activation.Id, activation.Customer?.Email, _clock.GetUtcNow()));
    }

    public async Task<LicenseCheck> ValidateAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostAsync("licenses/validate", new { license_key = record.Key, license_key_instance_id = record.InstanceId }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return LicenseCheck.Invalid;
            // Anything unexpected (server errors, rate limits, a changed API) keeps the offline grace
            // period running rather than taking Pro away from a paying customer.
            if (!response.IsSuccessStatusCode)
                return LicenseCheck.Unreachable;
            var result = await response.Content.ReadFromJsonAsync<ValidateResponse>(cancellationToken);
            return result?.Valid switch
            {
                true => LicenseCheck.Valid,
                false => LicenseCheck.Invalid,
                null => LicenseCheck.Unreachable,
            };
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
                                  || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return LicenseCheck.Unreachable;
        }
    }

    public async Task<bool> TryDeactivateAsync(string key, string instanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostAsync("licenses/deactivate", new { license_key = key, license_key_instance_id = instanceId }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private Task<HttpResponseMessage> PostAsync(string endpoint, object body, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(new Uri(server.BaseUri, endpoint), body, cancellationToken);

    internal sealed class ActivateResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("product")] public ProductInfo? Product { get; set; }
        [JsonPropertyName("customer")] public CustomerInfo? Customer { get; set; }
    }

    internal sealed class ProductInfo
    {
        [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    }

    internal sealed class CustomerInfo
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    internal sealed class ValidateResponse
    {
        [JsonPropertyName("valid")] public bool? Valid { get; set; }
    }
}
