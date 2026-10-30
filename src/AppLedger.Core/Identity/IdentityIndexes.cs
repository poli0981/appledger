using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// One entry in a launcher or package-manager index: an identifier the launcher owns, and the directory it
/// claims on disk.
/// </summary>
/// <param name="Id">The launcher's own identifier — a Steam appid, an Epic <c>AppName</c>, a Scoop app name.</param>
/// <param name="InstallLocation">The canonical directory the manifest claims.</param>
/// <param name="Name">Display name, when the manifest carries one.</param>
public sealed record LauncherIndexEntry(string Id, string InstallLocation, string? Name = null);

/// <summary>One Uninstall registry entry, reduced to the four values identity can use.</summary>
/// <param name="Key">The registry key name, which becomes the <c>uninst:</c> suffix.</param>
/// <param name="DisplayName">The <c>DisplayName</c> value, used as a display name.</param>
/// <param name="InstallLocation">The <c>InstallLocation</c> value, when the installer wrote one.</param>
/// <param name="DisplayIcon">The <c>DisplayIcon</c> value, which usually points at the main executable.</param>
/// <param name="Publisher">The <c>Publisher</c> value.</param>
public sealed record UninstallIndexEntry(
    string Key,
    string? DisplayName = null,
    string? InstallLocation = null,
    string? DisplayIcon = null,
    string? Publisher = null);

/// <summary>
/// Everything the resolver knows that did not come from the process itself: the registry, the store, the
/// launcher manifests, the package managers, and the two ETW-derived signals the anti-cheat rules need.
/// </summary>
/// <remarks>
/// This is a snapshot, deliberately. Building it means reading the registry and walking manifest
/// directories, which is Infrastructure's job and happens at Agent start and on change notification
/// (docs/03_APP_IDENTITY.md §Caching &amp; invalidation). The resolver is handed a finished one, which is
/// also what lets the S2 fixtures describe a whole synthetic machine as data.
/// </remarks>
public sealed record IdentityIndexes
{
    /// <summary>Uninstall registry entries, from both the 64- and 32-bit views.</summary>
    public IReadOnlyList<UninstallIndexEntry> Uninstall { get; init; } = [];

    /// <summary>Installed MSIX package family names.</summary>
    public IReadOnlySet<string> Msix { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Steam applications, keyed by the directory under <c>steamapps\common</c>.</summary>
    public IReadOnlyList<LauncherIndexEntry> Steam { get; init; } = [];

    /// <summary>Epic applications, from the <c>.item</c> manifests.</summary>
    public IReadOnlyList<LauncherIndexEntry> Epic { get; init; } = [];

    /// <summary>GOG applications, from <c>goggame-*.info</c>.</summary>
    public IReadOnlyList<LauncherIndexEntry> Gog { get; init; } = [];

    /// <summary>itch.io applications, from <c>.itch/receipt.json.gz</c>.</summary>
    public IReadOnlyList<LauncherIndexEntry> Itch { get; init; } = [];

    /// <summary>Scoop app directories.</summary>
    public IReadOnlyList<LauncherIndexEntry> Scoop { get; init; } = [];

    /// <summary>Chocolatey lib directories.</summary>
    public IReadOnlyList<LauncherIndexEntry> Choco { get; init; } = [];

    /// <summary>Kernel drivers seen through ETW <c>ImageLoad</c>, for the anti-cheat rules.</summary>
    public IReadOnlySet<string> LoadedDrivers { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Running Windows service names, for the anti-cheat and <c>svchost</c> rules.</summary>
    public IReadOnlySet<string> Services { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>An index that knows nothing, which is what Lite mode and a first start start from.</summary>
    public static IdentityIndexes Empty { get; } = new();

    /// <summary>
    /// The entry whose <see cref="LauncherIndexEntry.InstallLocation"/> contains the path, preferring the
    /// longest claim.
    /// </summary>
    /// <remarks>
    /// Longest wins because claims nest: a Steam library root contains <c>steamapps\common\Game</c>, and a
    /// Scoop <c>apps</c> directory contains one directory per app. Taking the first match would group every
    /// game under its library.
    /// </remarks>
    public static LauncherIndexEntry? ClaimOf(IReadOnlyList<LauncherIndexEntry> entries, string? canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrEmpty(canonicalPath))
        {
            return null;
        }

        LauncherIndexEntry? best = null;
        foreach (var entry in entries)
        {
            if (!Covers(entry.InstallLocation, canonicalPath))
            {
                continue;
            }

            if (best is null || entry.InstallLocation.Length > best.InstallLocation.Length)
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>
    /// The Uninstall entry that claims this image, and how strongly. <c>InstallLocation</c> is the only
    /// value an installer is expected to write correctly, so it is tried first and carries the higher
    /// confidence; <c>DisplayIcon</c> is a fallback because it usually names the main executable
    /// (docs/03_APP_IDENTITY.md §Confidence).
    /// </summary>
    public UninstallMatch? ClaimOfUninstall(string? canonicalImagePath)
    {
        if (string.IsNullOrEmpty(canonicalImagePath))
        {
            return null;
        }

        UninstallIndexEntry? byLocation = null;
        UninstallIndexEntry? byIcon = null;

        foreach (var entry in Uninstall)
        {
            if (Covers(entry.InstallLocation, canonicalImagePath)
                && (byLocation is null || entry.InstallLocation!.Length > byLocation.InstallLocation!.Length))
            {
                byLocation = entry;
                continue;
            }

            if (byIcon is null && IconNames(entry.DisplayIcon, canonicalImagePath))
            {
                byIcon = entry;
            }
        }

        return byLocation is not null
            ? new UninstallMatch(byLocation, Confidence.UninstallInstallLocation)
            : byIcon is not null ? new UninstallMatch(byIcon, Confidence.UninstallWeakMatch) : null;
    }

    /// <summary>True when the image path equals, or sits under, a directory an index entry claims.</summary>
    private static bool Covers(string? claim, string canonicalPath) =>
        !string.IsNullOrEmpty(claim)
        && (PathRules.SamePath(claim, canonicalPath) || PathRules.IsUnder(canonicalPath, claim));

    /// <summary>
    /// True when <c>DisplayIcon</c> names this image. The value often carries an icon index
    /// (<c>...\app.exe,0</c>) and is sometimes quoted, so it is cut back to the path before comparing.
    /// </summary>
    private static bool IconNames(string? displayIcon, string canonicalImagePath)
    {
        if (string.IsNullOrEmpty(displayIcon))
        {
            return false;
        }

        var value = displayIcon.Trim().Trim('"');
        var comma = value.LastIndexOf(',');
        if (comma > 2)
        {
            value = value[..comma];
        }

        return PathRules.SamePath(value.Trim(), canonicalImagePath);
    }
}

/// <summary>An Uninstall entry that claimed an image, with the confidence its match kind earns.</summary>
/// <param name="Entry">The claiming entry.</param>
/// <param name="Confidence">0.90 for an <c>InstallLocation</c> match, 0.80 for a weaker one.</param>
public sealed record UninstallMatch(UninstallIndexEntry Entry, double Confidence);
