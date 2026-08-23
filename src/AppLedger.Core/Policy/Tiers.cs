namespace AppLedger.Core.Policy;

/// <summary>
/// Path tiers from docs/11_SAFETY_POLICY.md §Path tiers. Every path that crosses the pipe is
/// canonicalized and tiered before use; no code outside <c>IPolicyGuard</c> may make this decision.
/// </summary>
public enum PathTier
{
    /// <summary>
    /// Protected OS locations (Windows, WindowsApps, $Recycle.Bin, System Volume Information, Recovery,
    /// Config.Msi, unmapped device volumes). Never a pickable or scannable app root, never enumerated,
    /// never listed by name — file lists show a single "(Windows)" bucket.
    /// </summary>
    ProtectedOs = 0,

    /// <summary>
    /// Sensitive user data (credential stores, key material, browser profile secrets, password vaults).
    /// Sizes are counted; names are never stored or sent, and the location is never opened.
    /// </summary>
    SensitiveUserData = 1,

    /// <summary>
    /// Everything outside the data root: readable, but write-protected for us by construction, because
    /// Infrastructure has no write adapter for arbitrary paths.
    /// </summary>
    WriteProtected = 2,

    /// <summary>Ordinary paths: readable and scannable.</summary>
    Normal = 3,
}

/// <summary>
/// Process tiers from docs/11_SAFETY_POLICY.md §Process access tiers. Only two exist: there is no
/// Tier 0/1 for processes, and the tier stored on an app row is always one of these two.
/// </summary>
public enum ProcessTier
{
    /// <summary>
    /// Anti-cheat–protected and PPL processes. <b>No <c>OpenProcess</c> at all</b>: identity comes from the
    /// ETW image name and launcher manifests, counters from the system-wide snapshot, and command line,
    /// token and package report "(zero-touch)".
    /// </summary>
    ZeroTouch = 2,

    /// <summary>
    /// Everything else: one <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle per instance for enrichment,
    /// closed immediately.
    /// </summary>
    Normal = 3,
}

/// <summary>
/// Why a path was rejected or restricted. Tier-0 and Tier-1 reasons are deliberately generic: reporting
/// the matched rule would turn the policy into an oracle for what we consider sensitive
/// (docs/11_SAFETY_POLICY.md §Canonicalization, step 6).
/// </summary>
public enum PathDenyReason
{
    /// <summary>The path is usable.</summary>
    None = 0,

    /// <summary>Under a protected OS root.</summary>
    ProtectedOs,

    /// <summary>Under a sensitive user-data location.</summary>
    SensitiveUserData,

    /// <summary>Empty or whitespace.</summary>
    Empty,

    /// <summary>Contains control characters or characters Windows cannot represent in a path.</summary>
    InvalidCharacters,

    /// <summary>Not rooted, or contains traversal that cannot be resolved without a working directory.</summary>
    NotRooted,

    /// <summary>A UNC or <c>\\?\UNC\</c> path. No network paths in v1.</summary>
    NetworkPath,

    /// <summary>A raw device path that we did not produce ourselves from the ETW device-path mapper.</summary>
    DevicePath,

    /// <summary>Longer than the platform allows even with long-path support.</summary>
    TooLong,
}
