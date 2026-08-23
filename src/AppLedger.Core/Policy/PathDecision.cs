namespace AppLedger.Core.Policy;

/// <summary>
/// The result of running a raw path through the policy: what it canonicalizes to, which tier it lands in,
/// and whether it may be used. Produced only by <see cref="IPolicyGuard"/> (docs/11_SAFETY_POLICY.md).
/// </summary>
/// <param name="Canonical">
/// The canonical form, or null when the input was rejected outright. For Tier-1 results this is still
/// filled in for internal comparison, but callers must not put it in output that leaves the Agent —
/// see <see cref="SafeToDisplay"/>.
/// </param>
/// <param name="Tier">The tier the canonical path falls into.</param>
/// <param name="Allowed">Whether the caller may read or scan this path.</param>
/// <param name="Reason">Why, when <paramref name="Allowed"/> is false or the tier is restricted.</param>
/// <param name="Unresolved">
/// True when reparse points could not be collapsed (typically access denied). Such a path is treated as
/// Tier 0 if its lexical form is already under a Tier-0 root, and Tier 3 with a warning otherwise.
/// </param>
public readonly record struct PathDecision(
    string? Canonical,
    PathTier Tier,
    bool Allowed,
    PathDenyReason Reason,
    bool Unresolved)
{
    /// <summary>A rejected path: nothing to canonicalize, nothing to report beyond the reason.</summary>
    public static PathDecision Rejected(PathDenyReason reason) =>
        new(null, PathTier.ProtectedOs, Allowed: false, reason, Unresolved: false);

    /// <summary>An ordinary, usable path.</summary>
    public static PathDecision Normal(string canonical, bool unresolved = false) =>
        new(canonical, PathTier.Normal, Allowed: true, PathDenyReason.None, unresolved);

    /// <summary>
    /// True when the canonical path may appear in anything the user or a log can see. Tier 0 and Tier 1
    /// paths never may: they are reported as sizes and a generic kind, with <c>path = null</c>
    /// (docs/07_IPC.md §Payload rules, docs/09_DISK_SCANNER.md §Output).
    /// </summary>
    public bool SafeToDisplay => Canonical is not null && Tier >= PathTier.WriteProtected;

    /// <summary>The path if it is safe to display, otherwise null.</summary>
    public string? DisplayPath => SafeToDisplay ? Canonical : null;
}
