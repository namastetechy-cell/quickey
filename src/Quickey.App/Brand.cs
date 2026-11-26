using Quickey.Core.Licensing;

namespace Quickey.App;

/// <summary>
/// Store and website details. Licenses are sold through Dodo Payments. Test-mode and live-mode products
/// have different IDs: when the store goes live, switch <see cref="LicenseServer"/> to
/// <see cref="LicenseServer.Live"/> with the live product IDs.
/// </summary>
public static class Brand
{
    public const string Name = "Quickey";

    /// <summary>Quickey Pro ($) and Quickey Pro (India, ₹). A key from either one unlocks Pro.</summary>
    public static readonly LicenseServer LicenseServer = new(LicenseServer.Test, ["pdt_0NsmUkEZZQ8KjdF2dYczF", "pdt_0NsmVmT0rlv3nM3wjJy0E"]);

    public const string Website = "https://namastetechy-cell.github.io/quickey-releases/";

    /// <summary>"Get Pro" opens the website's pricing, which offers the dollar and India (UPI) checkouts.</summary>
    public static readonly string CheckoutUrl = Website + "#pricing";

    public static readonly string SupportEmail = "";

    /// <summary>Public repo whose GitHub Releases hold the installers; also the auto-update feed.</summary>
    public const string UpdateFeed = "https://github.com/namastetechy-cell/quickey-releases";

    /// <summary>Where people download the installer.</summary>
    public const string DownloadUrl = UpdateFeed + "/releases/latest/download/Quickey-win-Setup.exe";

    public static bool IsStoreConfigured => LicenseServer.ProductIds.Count > 0;
}
