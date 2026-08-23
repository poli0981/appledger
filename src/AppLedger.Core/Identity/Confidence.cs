namespace AppLedger.Core.Identity;

/// <summary>
/// The confidence table of docs/03_APP_IDENTITY.md. The UI shows a "?" badge below
/// <see cref="NeedsReview"/> together with a one-click "Assign to app…" action, so these numbers are
/// user-visible behaviour rather than internal bookkeeping.
/// </summary>
public static class Confidence
{
    /// <summary>User override, MSIX, and launcher-manifest identities: unambiguous.</summary>
    public const double Certain = 1.00;

    /// <summary>A catalog rule matched.</summary>
    public const double CatalogRule = 0.95;

    /// <summary>An Uninstall key whose <c>InstallLocation</c> contains the image.</summary>
    public const double UninstallInstallLocation = 0.90;

    /// <summary>A Scoop, Chocolatey or winget package directory.</summary>
    public const double PackageManager = 0.90;

    /// <summary>A script or module identified from a runtime's command line.</summary>
    public const double Script = 0.85;

    /// <summary>An Uninstall key matched only through <c>DisplayIcon</c> or <c>UninstallString</c>.</summary>
    public const double UninstallWeakMatch = 0.80;

    /// <summary>PE product name plus Authenticode signer, with no registry or manifest backing.</summary>
    public const double PeAndSigner = 0.60;

    /// <summary>The install-root fallback.</summary>
    public const double RootFallback = 0.30;

    /// <summary>An instance whose resolution threw: recorded with an <c>IdentityError</c> event.</summary>
    public const double ResolutionFailed = 0.10;

    /// <summary>Strictly below this value the UI offers the user a correction; 0.60 itself does not prompt.</summary>
    public const double NeedsReview = 0.60;

    /// <summary>Adoption inherits the parent's confidence, discounted one step.</summary>
    public const double AdoptionFactor = 0.9;

    /// <summary>The confidence a freshly resolved identity carries, before adoption discounts.</summary>
    public static double ForSource(AppSource source) => source switch
    {
        AppSource.User => Certain,
        AppSource.System => Certain,
        AppSource.Msix => Certain,
        AppSource.Steam => Certain,
        AppSource.Epic => Certain,
        AppSource.Gog => Certain,
        AppSource.Itch => Certain,
        AppSource.Catalog => CatalogRule,
        AppSource.Uninstall => UninstallInstallLocation,
        AppSource.Scoop => PackageManager,
        AppSource.Choco => PackageManager,
        AppSource.Winget => PackageManager,
        AppSource.Script => Script,
        AppSource.Root => RootFallback,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown app source."),
    };

    /// <summary>The confidence a child inherits when it is adopted into its parent's app.</summary>
    public static double Adopted(double parentConfidence) => parentConfidence * AdoptionFactor;

    /// <summary>True when the UI should show the "?" badge and offer a manual assignment.</summary>
    public static bool ShouldPromptUser(double confidence) => confidence < NeedsReview;
}
