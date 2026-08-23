using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// Everything the resolver is allowed to know about one process instance.
/// </summary>
/// <remarks>
/// The split matters. <see cref="ImageFileName"/>, <see cref="Key"/>, <see cref="Parent"/> and
/// <see cref="SessionId"/> come from ETW and the system-wide snapshot and cost nothing. Everything else
/// needs a <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle and is therefore <b>null for a Tier-2
/// process</b>, where we open no handle at all (docs/11_SAFETY_POLICY.md §Process access tiers). Any
/// resolution step that reads one of those fields must cope with null rather than assume enrichment ran.
/// </remarks>
public sealed record ProcessFacts
{
    /// <summary>The process instance.</summary>
    public required ProcessKey Key { get; init; }

    /// <summary>The image file name without a path, e.g. <c>chrome.exe</c>. Always available.</summary>
    public required string ImageFileName { get; init; }

    /// <summary>The parent instance, when one is known and passed the PID-reuse guard.</summary>
    public ProcessKey? Parent { get; init; }

    /// <summary>The logon session id. Used to keep other users' processes out by default.</summary>
    public int SessionId { get; init; }

    /// <summary>Canonical full image path. Null for Tier-2 processes and when the handle could not be opened.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Full command line. Null for Tier-2, PPL, and when command-line storage is disabled.</summary>
    public string? CommandLine { get; init; }

    /// <summary>MSIX package family name, when the process is packaged.</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>Authenticode signer subject common name, when the image is signed and was verified.</summary>
    public string? Signer { get; init; }

    /// <summary>PE <c>ProductName</c>, used for the display name of a <c>root:</c> fallback.</summary>
    public string? ProductName { get; init; }

    /// <summary>PE <c>CompanyName</c>.</summary>
    public string? CompanyName { get; init; }

    /// <summary>PE <c>FileDescription</c>.</summary>
    public string? FileDescription { get; init; }

    /// <summary>The access tier decided by <see cref="IPolicyGuard"/> before any enrichment was attempted.</summary>
    public ProcessTier Tier { get; init; } = ProcessTier.Normal;
}

/// <summary>One step of the resolution pipeline, recorded so "why is this grouped here?" is answerable.</summary>
public enum ResolutionStep
{
    /// <summary>Step 1: the image path is under a Tier-0 root.</summary>
    ProtectedOsPath,

    /// <summary>Step 2: a catalog host rule matched.</summary>
    HostRule,

    /// <summary>Step 3: the process is MSIX-packaged.</summary>
    PackageIdentity,

    /// <summary>Step 4: a launcher manifest claims the install directory.</summary>
    LauncherManifest,

    /// <summary>Step 5: a catalog app rule matched.</summary>
    CatalogRule,

    /// <summary>Step 6: an Uninstall registry entry claims the image.</summary>
    UninstallIndex,

    /// <summary>Step 7: the image sits inside a package manager's directory layout.</summary>
    PackageManager,

    /// <summary>Step 8: the instance was adopted into its parent's app.</summary>
    ParentAdoption,

    /// <summary>Step 9: identity built from PE metadata and the install-root heuristic.</summary>
    PeMetadata,

    /// <summary>Step 10: the install-root hash fallback.</summary>
    RootFallback,

    /// <summary>A user override decided this, ahead of everything else.</summary>
    UserOverride,

    /// <summary>Resolution threw; the instance fell back with an <c>IdentityError</c> event.</summary>
    Failed,
}

/// <summary>
/// One piece of evidence behind a resolution. Stored as JSON in
/// <c>process_instances.identity_evidence</c> at Debug diagnostics level only, because it can contain
/// paths and command-line fragments (docs/12_PRIVACY_AND_RETENTION.md §Data inventory).
/// </summary>
/// <param name="Step">Which pipeline step produced it.</param>
/// <param name="Detail">A short human-readable reason, e.g. the rule id or the matched root.</param>
public readonly record struct IdentityEvidence(ResolutionStep Step, string Detail);

/// <summary>What the resolver concluded for one process instance.</summary>
public sealed record ResolutionResult
{
    /// <summary>The app this instance belongs to.</summary>
    public required AppId AppId { get; init; }

    /// <summary>Which source won.</summary>
    public required AppSource Source { get; init; }

    /// <summary>Confidence, per docs/03 §Confidence and discounted by adoption.</summary>
    public required double Confidence { get; init; }

    /// <summary>The step that decided it.</summary>
    public required ResolutionStep DecidedBy { get; init; }

    /// <summary>Display name for a newly created app row.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Canonical install root, or null for `sys:*` and `script:` identities.</summary>
    public string? InstallRoot { get; init; }

    /// <summary>Losing candidates, kept so a later merge is cheap (<c>apps.aliases_json</c>).</summary>
    public IReadOnlyList<AppId> Aliases { get; init; } = [];

    /// <summary>Why this identity was chosen.</summary>
    public IReadOnlyList<IdentityEvidence> Evidence { get; init; } = [];

    /// <summary>True when the UI should offer a manual correction.</summary>
    public bool NeedsUserReview => Confidence.ShouldPrompt();
}

/// <summary>Small helper so call sites read as intent rather than as a comparison.</summary>
internal static class ConfidenceExtensions
{
    internal static bool ShouldPrompt(this double confidence) => Confidence.ShouldPromptUser(confidence);
}

/// <summary>
/// Maps process instances to apps. The implementation is the hardest part of the product and is gated by
/// spike S2, so it lands with its fixture suite rather than ahead of it (docs/20_SPIKES.md).
/// </summary>
public interface IIdentityResolver
{
    /// <summary>
    /// Resolves one instance. Never throws for unresolvable input: an instance that cannot be identified
    /// comes back as a <c>root:</c> fallback with low confidence.
    /// </summary>
    ResolutionResult Resolve(ProcessFacts facts);

    /// <summary>
    /// Drops cached resolutions so live instances are re-resolved. Called when the catalog updates or the
    /// user changes an override; history rows keep their original ids either way.
    /// </summary>
    void Invalidate();
}
