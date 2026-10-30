using System.IO.Compression;
using System.Text.Json;
using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;
using Microsoft.Win32;

namespace AppLedger.Infrastructure.Identity;

/// <summary>
/// Reads the launcher manifests that step 4 of docs/03_APP_IDENTITY.md consults, so a game is its own app
/// rather than a directory under its launcher.
/// </summary>
/// <remarks>
/// Every launcher is asked where its games are rather than being guessed at, and the answer always comes
/// from the launcher's own bookkeeping: Steam's library list, Epic's <c>.item</c> manifests, GOG's registry
/// entries, itch's app directory. Scanning for games instead would mean walking volumes, which is both
/// slow and a path-policy problem.
/// <para>
/// Each returned <see cref="LauncherIndexEntry.InstallLocation"/> is the directory the launcher claims,
/// canonicalized and tiered. <see cref="IdentityIndexes.ClaimOf"/> resolves nested claims by the longest
/// one, so a Steam <i>library</i> root must never be returned as an entry — it contains every game in it,
/// and the game would lose its own identity to the library.
/// </para>
/// </remarks>
public sealed class LauncherManifestReader
{
    private readonly IPolicyGuard _policy;
    private readonly KnownFolders _folders;

    /// <summary>Creates a reader over the current user's folders.</summary>
    public LauncherManifestReader(IPolicyGuard policy, KnownFolders folders)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(folders);
        _policy = policy;
        _folders = folders;
    }

    /// <summary>Steam applications, from every library the client knows about.</summary>
    public IReadOnlyList<LauncherIndexEntry> ReadSteam()
    {
        var entries = new List<LauncherIndexEntry>();

        foreach (var library in SteamLibraries())
        {
            var steamapps = Path.Combine(library, "steamapps");

            foreach (var manifest in Files(steamapps, "appmanifest_*.acf"))
            {
                if (ReadText(manifest) is not { } text)
                {
                    continue;
                }

                var appId = ValveKeyValues.Value(text, "appid");
                var installDir = ValveKeyValues.Value(text, "installdir");

                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir))
                {
                    continue;
                }

                // The claim is the game's own directory under steamapps\common, never the library root.
                Add(entries, appId, Path.Combine(steamapps, "common", installDir), ValveKeyValues.Value(text, "name"));
            }
        }

        return entries;
    }

    /// <summary>Epic applications, from the launcher's <c>.item</c> manifests.</summary>
    public IReadOnlyList<LauncherIndexEntry> ReadEpic()
    {
        var entries = new List<LauncherIndexEntry>();

        if (_folders.ProgramData is not { } programData)
        {
            return entries;
        }

        var manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");

        foreach (var file in Files(manifests, "*.item"))
        {
            if (ParseEpicItem(file) is { } claim)
            {
                Add(entries, claim.Id, claim.Location, claim.Name);
            }
        }

        return entries;
    }

    /// <summary>
    /// One Epic <c>.item</c> manifest, or null when it is not one. <c>AppName</c> rather than
    /// <c>DisplayName</c>: docs/03 §app_id scheme is explicit that the id is the manifest's
    /// <c>AppName</c>, because a display name is localized and changes under the app.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="ReadEpic"/> so the parse can be tested without an Epic install, which is
    /// the only part of it that can be wrong on a machine that has one.
    /// </remarks>
    internal static ManifestClaim? ParseEpicItem(string file)
    {
        if (ReadJson(file) is not { } root)
        {
            return null;
        }

        var name = String(root, "AppName");
        var location = String(root, "InstallLocation");

        return name is not null && location is not null
            ? new ManifestClaim(name, location, String(root, "DisplayName"))
            : null;
    }

    /// <summary>
    /// The claim in one itch app directory, read from <c>.itch/receipt.json.gz</c> — which is where docs/03
    /// says to look, because the directory name is the app's slug rather than the numeric game id the
    /// <c>itch:</c> scheme uses.
    /// </summary>
    internal static ManifestClaim? ParseItchReceipt(string directory)
    {
        var receipt = Path.Combine(directory, ".itch", "receipt.json.gz");

        if (ReadGzipJson(receipt) is not { } root
            || !root.TryGetProperty("game", out var game)
            || !game.TryGetProperty("id", out var id))
        {
            return null;
        }

        var value = id.ValueKind == JsonValueKind.Number
            ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : id.GetString();

        return string.IsNullOrWhiteSpace(value)
            ? null
            : new ManifestClaim(value, directory, String(game, "title"));
    }

    /// <summary>What a manifest says, before path policy has had a look at it.</summary>
    /// <param name="Id">The launcher's own identifier.</param>
    /// <param name="Location">The directory the manifest claims, as written.</param>
    /// <param name="Name">A display name, when the manifest carries one.</param>
    internal readonly record struct ManifestClaim(string Id, string Location, string? Name);

    /// <summary>
    /// GOG applications, from <c>HKLM\SOFTWARE\GOG.com\Games</c>. The registry is used rather than a hunt
    /// for <c>goggame-*.info</c> files, which would mean walking disks to find the directories the registry
    /// already names; the <c>.info</c> file is what GOG writes <i>into</i> those directories.
    /// </summary>
    public IReadOnlyList<LauncherIndexEntry> ReadGog()
    {
        var entries = new List<LauncherIndexEntry>();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");

                if (games is null)
                {
                    continue;
                }

                foreach (var id in games.GetSubKeyNames())
                {
                    using var game = games.OpenSubKey(id);
                    if (game?.GetValue("path") is string path)
                    {
                        Add(entries, game.GetValue("gameID") as string ?? id, path, game.GetValue("gameName") as string);
                    }
                }
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                // A view we cannot read is a smaller index, not a failure.
            }
        }

        return entries;
    }

    /// <summary>
    /// itch.io applications. The id lives in <c>.itch/receipt.json.gz</c> inside each app directory, which
    /// is where docs/03 says to look — the directory name is the app's slug, not the numeric game id the
    /// <c>itch:</c> scheme uses.
    /// </summary>
    public IReadOnlyList<LauncherIndexEntry> ReadItch()
    {
        var entries = new List<LauncherIndexEntry>();

        if (_folders.RoamingAppData is not { } roaming)
        {
            return entries;
        }

        foreach (var directory in Directories(Path.Combine(roaming, "itch", "apps")))
        {
            if (ParseItchReceipt(directory) is { } claim)
            {
                Add(entries, claim.Id, claim.Location, claim.Name);
            }
        }

        return entries;
    }

    /// <summary>
    /// Every Steam library directory. The client's own <c>libraryfolders.vdf</c> is the authority, and the
    /// install directory itself is always a library even when the file does not list it.
    /// </summary>
    private static List<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = SteamPath();

        if (steam is null)
        {
            return [];
        }

        var libraries = new List<string>();
        if (seen.Add(steam))
        {
            libraries.Add(steam);
        }

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (ReadText(vdf) is { } text)
        {
            foreach (var path in ValveKeyValues.Values(text, "path"))
            {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                {
                    libraries.Add(path);
                }
            }
        }

        return libraries;
    }

    private static string? SteamPath()
    {
        // HKCU first: it records the install the current user actually runs, which is the one whose
        // libraries matter for processes in this session.
        foreach (var (hive, view, path, value) in new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(path);

                if (key?.GetValue(value) is string found && !string.IsNullOrWhiteSpace(found))
                {
                    // SteamPath is written with forward slashes often enough to matter.
                    return found.Replace('/', '\\');
                }
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Adds an entry whose directory survives <see cref="IPolicyGuard"/>. A claim we cannot canonicalize is
    /// dropped rather than stored raw: <see cref="IdentityIndexes.ClaimOf"/> compares it against canonical
    /// image paths, so a raw one would silently never match.
    /// </summary>
    private void Add(List<LauncherIndexEntry> entries, string id, string location, string? name)
    {
        var decision = _policy.Evaluate(location);

        if (decision is { Allowed: true, Canonical: not null } && decision.Tier != PathTier.ProtectedOs)
        {
            entries.Add(new LauncherIndexEntry(id.Trim(), decision.Canonical, name?.Trim()));
        }
    }

    private static IEnumerable<string> Files(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return [];
        }
    }

    private static IEnumerable<string> Directories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return [];
        }
    }

    private static string? ReadText(string file)
    {
        try
        {
            // Manifests are small; a pathological one is someone else's file and not worth reading into
            // the Agent's working set, which is the budget that binds (docs/05 §Budget).
            var info = new FileInfo(file);
            return info.Exists && info.Length <= 1024 * 1024 ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return null;
        }
    }

    private static JsonElement? ReadJson(string file)
    {
        if (ReadText(file) is not { } text)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ReadGzipJson(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 1024 * 1024)
            {
                return null;
            }

            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(gzip);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (IsExpectedFailure(ex) || ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// What reading someone else's files and registry legitimately throws: a game uninstalled between the
    /// enumeration and the read, a permission we do not have, a path longer than the API accepts. Anything
    /// else is a bug here and is left to propagate.
    /// </summary>
    private static bool IsExpectedFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or ObjectDisposedException;
}
