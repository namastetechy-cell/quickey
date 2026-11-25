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

    public static readonly LicenseProduct LicenseProduct = new(StoreId: 514914, ProductId: 1536053);

    /// <summary>Lemon Squeezy checkout link for Quickey Pro. Empty hides the "Get Pro" buttons.</summary>
    public static readonly string CheckoutUrl = "https://getquickey.lemonsqueezy.com/checkout/buy/3206e744-ac42-4584-bb6e-55e3a82de228";

    public static readonly string SupportEmail = "";

    /// <summary>Public repo whose GitHub Releases hold the installers; also the auto-update feed.</summary>
    public const string UpdateFeed = "https://github.com/namastetechy-cell/quickey-releases";

    /// <summary>Where people download the installer.</summary>
    public const string DownloadUrl = UpdateFeed + "/releases/latest/download/Quickey-win-Setup.exe";

    public static bool IsStoreConfigured => LicenseProduct.StoreId != 0 && LicenseProduct.ProductId != 0;
}
