using AppLedger.Core.Catalog;

namespace AppLedger.Core.Policy;

/// <summary>
/// Classifies an already-canonical path into a <see cref="PathTier"/> against a set of roots.
/// </summary>
/// <remarks>
/// The roots themselves are discovered with <c>SHGetKnownFolderPath</c> in Infrastructure — never
/// hard-coded to <c>C:\</c> — and the catalog may extend the Tier-0 and Tier-1 lists but not shrink them
/// (docs/11_SAFETY_POLICY.md §Path tiers). The comparison itself is pure, so the whole classification
/// table is testable on any OS with fixture roots.
/// </remarks>
public sealed class PathTierTable
{
    private readonly string[] _protectedOsRoots;
    private readonly PathGlob[] _protectedGlobs;
    private readonly PathGlob[] _sensitiveGlobs;
    private readonly string[] _sensitiveRoots;
    private readonly string? _dataRoot;

    /// <summary>Creates a table over the given roots.</summary>
    /// <param name="protectedOsRoots">Canonical Tier-0 roots.</param>
    /// <param name="sensitiveRoots">Canonical Tier-1 directories.</param>
    /// <param name="sensitiveGlobs">Tier-1 globs from the catalog, already environment-expanded.</param>
    /// <param name="dataRoot">The AppLedger data root, the only place we ever write.</param>
    /// <param name="protectedGlobs">
    /// Tier-0 globs from the catalog's <c>protected_paths</c>, already environment-expanded. They extend
    /// the built-in minimum; nothing here can remove a root passed in <paramref name="protectedOsRoots"/>.
    /// </param>
    public PathTierTable(
        IReadOnlyList<string> protectedOsRoots,
        IReadOnlyList<string> sensitiveRoots,
        IReadOnlyList<PathGlob> sensitiveGlobs,
        string? dataRoot = null,
        IReadOnlyList<PathGlob>? protectedGlobs = null)
    {
        ArgumentNullException.ThrowIfNull(protectedOsRoots);
        ArgumentNullException.ThrowIfNull(sensitiveRoots);
        ArgumentNullException.ThrowIfNull(sensitiveGlobs);

        _protectedOsRoots = [.. protectedOsRoots];
        _sensitiveRoots = [.. sensitiveRoots];
        _sensitiveGlobs = [.. sensitiveGlobs];
        _protectedGlobs = protectedGlobs is null ? [] : [.. protectedGlobs];
        _dataRoot = dataRoot;
    }

    /// <summary>
    /// Volume-relative directories that are Tier 0 on every drive, not just the system one: a recycle bin
    /// or a shadow-copy store on D: is no more scannable than the one on C:.
    /// </summary>
    public static IReadOnlyList<string> VolumeRelativeProtectedDirectories { get; } =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "Config.Msi",
        "$WinREAgent",
    ];

    /// <summary>The tier of a canonical path, with the generic reason code that may be reported.</summary>
    public PathTier Classify(string? canonicalPath, out PathDenyReason reason)
    {
        if (string.IsNullOrEmpty(canonicalPath))
        {
            reason = PathDenyReason.Empty;
            return PathTier.ProtectedOs;
        }

        foreach (var root in _protectedOsRoots)
        {
            if (PathRules.IsUnder(canonicalPath, root))
            {
                reason = PathDenyReason.ProtectedOs;
                return PathTier.ProtectedOs;
            }
        }

        foreach (var glob in _protectedGlobs)
        {
            if (glob.MatchesOrContains(canonicalPath))
            {
                reason = PathDenyReason.ProtectedOs;
                return PathTier.ProtectedOs;
            }
        }

        var volumeRoot = PathRules.VolumeRoot(canonicalPath);
        if (volumeRoot is not null)
        {
            foreach (var directory in VolumeRelativeProtectedDirectories)
            {
                if (PathRules.IsUnder(canonicalPath, volumeRoot + directory))
                {
                    reason = PathDenyReason.ProtectedOs;
                    return PathTier.ProtectedOs;
                }
            }
        }

        foreach (var root in _sensitiveRoots)
        {
            if (PathRules.IsUnder(canonicalPath, root))
            {
                reason = PathDenyReason.SensitiveUserData;
                return PathTier.SensitiveUserData;
            }
        }

        foreach (var glob in _sensitiveGlobs)
        {
            if (glob.MatchesOrContains(canonicalPath))
            {
                reason = PathDenyReason.SensitiveUserData;
                return PathTier.SensitiveUserData;
            }
        }

        reason = PathDenyReason.None;

        // Tier 2 exists to say "readable, but we have no writer for it". The data root is the exception.
        return _dataRoot is not null && PathRules.IsUnder(canonicalPath, _dataRoot)
            ? PathTier.Normal
            : PathTier.WriteProtected;
    }

    /// <summary>
    /// True when a directory may be enumerated by the disk scanner. Tier-0 roots are never scanned; a
    /// Tier-1 directory is measured but its entries are never named, which the scanner handles itself.
    /// </summary>
    public bool CanScan(string? canonicalPath) => Classify(canonicalPath, out _) != PathTier.ProtectedOs;

    /// <summary>True when the path is inside the data root.</summary>
    public bool IsInsideDataRoot(string? canonicalPath) =>
        _dataRoot is not null && PathRules.IsUnder(canonicalPath, _dataRoot);
}
