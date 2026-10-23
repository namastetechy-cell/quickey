using Quickey.Core.Licensing;

namespace Quickey.App;

/// <summary>
/// Store and website details. Fill these in from the Lemon Squeezy dashboard before shipping:
/// Store ID is under Settings → Stores, Product ID on the product's page. Until then activation
/// rejects every key (by design: a key must match this exact store and product).
/// </summary>
public static class Brand
{
    public const string Name = "Quickey";

    public static readonly LicenseProduct LicenseProduct = new(StoreId: 0, ProductId: 0);

    /// <summary>Lemon Squeezy checkout link for Quickey Pro. Empty hides the "Get Pro" buttons.</summary>
    public static readonly string CheckoutUrl = "";

    public static readonly string SupportEmail = "";

    public static bool IsStoreConfigured => LicenseProduct.StoreId != 0 && LicenseProduct.ProductId != 0;
}
