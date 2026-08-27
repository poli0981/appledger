using AppLedger.Core.Catalog;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Storage;

namespace AppLedger.Infrastructure.Policy;

/// <summary>
/// The Windows implementation of <see cref="IPolicyGuard"/>: the single authority for path and process
/// access decisions (docs/11_SAFETY_POLICY.md).
/// </summary>
/// <remarks>
/// It owns no rules of its own. The lexical rules are <see cref="PathRules"/>, the classification table is
/// <see cref="PathTierTable"/>, the process rules are <see cref="ProcessTierTable"/> — all pure Core code
/// with their own tests. What lives here is only the part that needs Windows: resolving the known-folder
/// roots and collapsing reparse points.
/// </remarks>
public sealed class PolicyGuard : IPolicyGuard
{
    private readonly PathTierTable _pathTiers;
    private readonly ProcessTierTable _processTiers;
    private readonly DataRoot _dataRoot;

    private PolicyGuard(PathTierTable pathTiers, ProcessTierTable processTiers, DataRoot dataRoot)
    {
        _pathTiers = pathTiers;
        _processTiers = processTiers;
        _dataRoot = dataRoot;
    }

    /// <summary>
    /// Builds a guard from this machine's known folders plus whatever a verified catalog adds.
    /// </summary>
    /// <param name="catalog">
    /// A verified catalog, or null to run on the built-in minimum alone. A catalog may only *extend* the
    /// Tier-0 and Tier-1 lists (docs/11 §Path tiers), which is enforced here by adding its entries to the
    /// built-ins rather than by letting it supply the whole list.
    /// </param>
    /// <param name="dataRoot">The data root, or null for this user's real one.</param>
    /// <param name="folders">Resolved known folders, or null for this user's real ones.</param>
    public static PolicyGuard Create(
        CatalogDocument? catalog = null,
        DataRoot? dataRoot = null,
        KnownFolders? folders = null)
    {
        var knownFolders = folders ?? KnownFolders.Current;
        var root = dataRoot ?? DataRoot.Default;
        var expander = new EnvExpander(knownFolders.CatalogVariables);

        var protectedRoots = new List<string>(knownFolders.ProtectedOsRoots);
        var sensitiveRoots = new List<string>(knownFolders.SensitiveRoots);
        var sensitiveGlobs = BuildBuiltInSensitiveGlobs(knownFolders);
        var protectedGlobs = new List<PathGlob>();

        if (catalog is not null)
        {
            AddGlobs(protectedGlobs, catalog.ProtectedPaths, expander);
            AddGlobs(sensitiveGlobs, catalog.SensitivePaths.Select(p => p.Glob), expander);
        }

        var pathTiers = new PathTierTable(
            protectedOsRoots: protectedRoots,
            sensitiveRoots: sensitiveRoots,
            sensitiveGlobs: sensitiveGlobs,
            dataRoot: root.Root,
            protectedGlobs: protectedGlobs);

        var processTiers = new ProcessTierTable(
            catalog?.ProtectedProcesses,
            catalog is null ? null : [.. catalog.AntiCheat.SelectMany(a => a.Dirs)]);

        return new PolicyGuard(pathTiers, processTiers, root);
    }

    /// <inheritdoc />
    public PathDecision Evaluate(string? rawPath)
    {
        // Step 1 and the lexical half of step 2 (docs/11 §Canonicalization). A shape we refuse never
        // reaches the file system at all.
        if (!PathRules.TryNormalize(rawPath, out var normalized, out var reason))
        {
            return PathDecision.Rejected(reason);
        }

        var canonical = PathCanonicalizer.Canonicalize(normalized);
        var tier = _pathTiers.Classify(canonical.Path, out var tierReason);

        // docs/11 step 3 says an unresolved path is "Tier 0 if its lexical form is under a Tier-0 root,
        // Tier 3 otherwise". We classify the lexical form through the full table instead, which agrees for
        // Tier 0 and is strictly safer for Tier 1: a credential store we could not open must not be
        // downgraded to an ordinary path and have its name reported.
        return new PathDecision(
            canonical.Path,
            tier,
            Allowed: tier >= PathTier.WriteProtected,
            tierReason,
            canonical.Unresolved);
    }

    /// <inheritdoc />
    public PathTier TierOf(string canonicalPath) => _pathTiers.Classify(canonicalPath, out _);

    /// <inheritdoc />
    public bool CanScan(string canonicalPath) => _pathTiers.CanScan(canonicalPath);

    /// <inheritdoc />
    public ProcessTier TierOfProcess(string? canonicalImagePath, string? imageFileName) =>
        _processTiers.Classify(canonicalImagePath, imageFileName);

    /// <inheritdoc />
    public bool IsInsideDataRoot(string canonicalPath) => _dataRoot.Contains(canonicalPath);

    /// <summary>
    /// The Tier-1 file patterns docs/11 §Path tiers lists by name: browser profile secrets. They are
    /// built-ins rather than catalog entries because a catalog that failed to load must not leave a
    /// browser's saved passwords classified as an ordinary file.
    /// </summary>
    private static List<PathGlob> BuildBuiltInSensitiveGlobs(KnownFolders folders)
    {
        string[] secretFileNames =
        [
            "Login Data*",   // Chromium saved passwords
            "Cookies*",      // Chromium cookie jar
            "Web Data*",     // Chromium autofill
            "key4.db",       // Firefox key store
            "logins.json",   // Firefox saved logins
            "cert9.db",      // Firefox certificate store
        ];

        var globs = new List<PathGlob>(secretFileNames.Length * 2);

        // Chromium profiles live under LocalAppData, Firefox under RoamingAppData, and forks disagree with
        // both. Covering the two profile roots costs nothing and misclassifies nothing: these file names
        // do not occur outside a browser profile.
        foreach (var profileRoot in new[] { folders.LocalAppData, folders.RoamingAppData })
        {
            if (string.IsNullOrEmpty(profileRoot))
            {
                continue;
            }

            foreach (var fileName in secretFileNames)
            {
                globs.Add(PathGlob.Parse(Path.Combine(profileRoot, "**", fileName)));
            }
        }

        return globs;
    }

    private static void AddGlobs(List<PathGlob> target, IEnumerable<string> patterns, EnvExpander expander)
    {
        foreach (var pattern in patterns)
        {
            // The catalog is strict-parsed and signature-verified before it gets here, so a pattern that
            // still fails to expand or parse is a bug in the validator, not user input to tolerate.
            target.Add(PathGlob.Parse(expander.Expand(pattern)));
        }
    }
}
