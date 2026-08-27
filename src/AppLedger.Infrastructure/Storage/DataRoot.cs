using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;

namespace AppLedger.Infrastructure.Storage;

/// <summary>
/// The single directory AppLedger is allowed to write to: <c>%LOCALAPPDATA%\AppLedgerData</c>
/// (ADR-15 — deliberately outside the Velopack install folder so uninstalling can keep the history).
/// </summary>
/// <remarks>
/// Everything that writes takes a <see cref="DataRoot"/> rather than composing a path itself, so "where
/// may we write?" has exactly one answer and the tests can point it somewhere harmless. The Tier-2 rule
/// of docs/11_SAFETY_POLICY.md §Path tiers — "read-only by construction, because Infrastructure has no
/// write adapter for arbitrary paths" — only holds as long as that stays true.
/// </remarks>
public sealed class DataRoot
{
    /// <summary>The folder name under LocalAppData. Not the install folder, which Velopack owns.</summary>
    public const string FolderName = "AppLedgerData";

    private static readonly Lazy<DataRoot> DefaultLazy = new(CreateDefault);

    /// <summary>Creates a root at an explicit location. The path is normalized but not created.</summary>
    /// <param name="root">An absolute directory path.</param>
    public DataRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!PathRules.TryNormalize(root, out var normalized, out var reason))
        {
            throw new ArgumentException($"'{root}' is not a usable data root ({reason}).", nameof(root));
        }

        Root = normalized;
        DatabasePath = Path.Combine(Root, "appledger.db");
        SettingsPath = Path.Combine(Root, "settings.json");
        LogsDirectory = Path.Combine(Root, "logs");
        CatalogDirectory = Path.Combine(Root, "catalog");
        CacheDirectory = Path.Combine(Root, "cache");
        IconCacheDirectory = Path.Combine(CacheDirectory, "icons");
    }

    /// <summary>The real data root for this user.</summary>
    public static DataRoot Default => DefaultLazy.Value;

    /// <summary>The canonical root directory.</summary>
    public string Root { get; }

    /// <summary>The SQLite database. Its <c>-wal</c> and <c>-shm</c> siblings live beside it.</summary>
    public string DatabasePath { get; }

    /// <summary>The UI-owned settings file mirrored into the <c>settings</c> table.</summary>
    public string SettingsPath { get; }

    /// <summary>Rolling Serilog files for both processes (docs/15_LOGGING.md).</summary>
    public string LogsDirectory { get; }

    /// <summary>The verified catalog copy the Agent actually loads.</summary>
    public string CatalogDirectory { get; }

    /// <summary>Regenerable caches. Everything here may be deleted without losing history.</summary>
    public string CacheDirectory { get; }

    /// <summary>Extracted app icons, one PNG per <c>app_id</c>.</summary>
    public string IconCacheDirectory { get; }

    /// <summary>
    /// Creates the root and its subdirectories if they are missing. The only directories AppLedger ever
    /// creates are these.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CatalogDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
    }

    /// <summary>
    /// True when a canonical path is the root itself or lies inside it. This is the precondition for every
    /// write and every delete.
    /// </summary>
    public bool Contains(string? canonicalPath) => PathRules.IsUnder(canonicalPath, Root);

    private static DataRoot CreateDefault()
    {
        var localAppData = KnownFolders.Current.LocalAppData
            ?? throw new InvalidOperationException(
                "FOLDERID_LocalAppData did not resolve, so there is nowhere to put the data root.");

        return new DataRoot(Path.Combine(localAppData, FolderName));
    }
}
