using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// Finds the directory that "is" an app on disk, by walking up from its executable until the next step
/// would leave a container that holds many unrelated apps (docs/03_APP_IDENTITY.md §Install root
/// heuristic).
/// </summary>
/// <remarks>
/// The boundaries are injected rather than hard-coded, both because they come from
/// <c>SHGetKnownFolderPath</c> and because that makes the whole table testable on any OS with fixture
/// roots. Getting this wrong is not a cosmetic bug: the install root is the fallback identity, the disk
/// footprint and the parent-adoption test, so a root one level too high merges unrelated apps and one
/// level too low splits an app into its own subfolders.
/// </remarks>
public sealed class InstallRootHeuristic
{
    /// <summary>
    /// Leaf directories that are an artefact of how the app was packaged rather than part of its identity.
    /// A Squirrel app reinstalls into a new <c>app-1.0.10</c> beside the old one, so treating that as the
    /// install root would give the same app a new identity on every update.
    /// </summary>
    public static IReadOnlySet<string> PackagingLeafNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "current",      // Velopack
            "bin",
            "x64",
            "x86",
            "win-x64",
            "win-arm64",
            "Release",
            "Debug",
        };

    private readonly string[] _boundaries;

    /// <summary>Creates a heuristic over the containers that end the upward walk.</summary>
    /// <param name="boundaries">
    /// Canonical directories that hold many unrelated apps: the Program Files roots, the user's profile
    /// and app-data roots, ProgramData, and every Tier-0 root. Volume roots and <c>steamapps\common</c>
    /// are recognised structurally and need not be listed.
    /// </param>
    public InstallRootHeuristic(IReadOnlyList<string> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        _boundaries = [.. boundaries.Where(b => !string.IsNullOrWhiteSpace(b))];
    }

    /// <summary>
    /// The install root for a canonical executable path, or null when the path is not usable. The result is
    /// always a directory, never the executable itself.
    /// </summary>
    public string? FromImagePath(string? canonicalImagePath)
    {
        if (string.IsNullOrEmpty(canonicalImagePath))
        {
            return null;
        }

        var directory = PathRules.Parent(canonicalImagePath);
        return directory is null ? null : FromDirectory(directory);
    }

    /// <summary>The install root for a directory already known to contain the app.</summary>
    public string? FromDirectory(string? canonicalDirectory)
    {
        if (string.IsNullOrEmpty(canonicalDirectory))
        {
            return null;
        }

        // An executable sitting directly in a boundary — a loose exe in the user profile, a stub in
        // ProgramData, anything at a volume root — has no install root of its own. Reporting the container
        // would make every such executable the same app. Checked before trimming, because a volume root is
        // "D:\" and trimming it to "D:" makes it unrecognisable.
        if (IsBoundary(canonicalDirectory) || IsVolumeRoot(canonicalDirectory))
        {
            return null;
        }

        var current = canonicalDirectory.TrimEnd('\\');

        while (true)
        {
            var parent = PathRules.Parent(current);

            // A volume root is a boundary, so whatever sits directly under it is the root.
            if (parent is null || IsBoundary(parent) || IsVolumeRoot(parent) || IsSteamCommon(parent))
            {
                break;
            }

            current = parent;
        }

        return SkipPackagingLeaves(current);
    }

    /// <summary>
    /// True when the last component is a packaging artefact worth stepping over. Public because the disk
    /// scanner needs the same judgement when it decides which directory to measure.
    /// </summary>
    public static bool IsPackagingLeaf(string? canonicalDirectory)
    {
        var leaf = canonicalDirectory is null ? null : PathRules.LeafName(canonicalDirectory);
        return leaf is not null && (PackagingLeafNames.Contains(leaf) || IsSquirrelVersionFolder(leaf));
    }

    /// <summary>
    /// Squirrel installs to <c>app-&lt;version&gt;</c>. Matching the shape rather than a list keeps it
    /// working for versions nobody has released yet.
    /// </summary>
    private static bool IsSquirrelVersionFolder(string leaf) =>
        leaf.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
        && leaf.Length > 4
        && char.IsAsciiDigit(leaf[4]);

    /// <summary>
    /// Walks up past packaging leaves, but never past a boundary — an app installed directly into
    /// <c>%LOCALAPPDATA%\current</c> keeps that directory rather than becoming LocalAppData itself.
    /// </summary>
    private string? SkipPackagingLeaves(string directory)
    {
        var current = directory;

        for (var depth = 0; depth < 4 && IsPackagingLeaf(current); depth++)
        {
            var parent = PathRules.Parent(current);
            if (parent is null || IsBoundary(parent) || IsVolumeRoot(parent))
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private bool IsBoundary(string directory) =>
        Array.Exists(_boundaries, b => PathRules.SamePath(directory, b));

    private static bool IsVolumeRoot(string directory) =>
        PathRules.SamePath(directory, PathRules.VolumeRoot(directory));

    /// <summary>
    /// <c>steamapps\common</c> holds one directory per installed game, so it is a boundary wherever a
    /// library happens to live — and Steam libraries live on any drive the user chooses.
    /// </summary>
    private static bool IsSteamCommon(string directory)
    {
        if (!string.Equals(PathRules.LeafName(directory), "common", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = PathRules.Parent(directory);
        return parent is not null
            && string.Equals(PathRules.LeafName(parent), "steamapps", StringComparison.OrdinalIgnoreCase);
    }
}
