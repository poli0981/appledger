using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AppLedger.Core.Identity;

/// <summary>
/// A stable application identifier: <c>&lt;prefix&gt;:&lt;value&gt;</c>, as specified by
/// docs/03_APP_IDENTITY.md. Stable across reinstalls and versions, and used as the key of every
/// metrics table, so its formatting rules are part of the storage contract rather than a detail.
/// </summary>
public readonly record struct AppId
{
    /// <summary>Number of hex characters kept from a SHA-256 digest in `script:` and `root:` ids.</summary>
    public const int HashLength = 16;

    private AppId(string value) => Value = value;

    /// <summary>The full id, e.g. <c>cat:discord</c>. Never null for a constructed value.</summary>
    public string Value { get; }

    /// <summary>The source that produced this id.</summary>
    public AppSource Source => AppSourceExtensions.FromPrefix(Prefix)
        ?? throw new InvalidOperationException($"'{Value}' has no known app-id prefix.");

    /// <summary>The prefix, without the colon.</summary>
    public string Prefix
    {
        get
        {
            var i = Value.IndexOf(':', StringComparison.Ordinal);
            return i <= 0 ? string.Empty : Value[..i];
        }
    }

    /// <summary>The part after the first colon.</summary>
    public string Suffix
    {
        get
        {
            var i = Value.IndexOf(':', StringComparison.Ordinal);
            return i < 0 ? string.Empty : Value[(i + 1)..];
        }
    }

    /// <summary>True for the `sys:*` family, which is never scanned as an app and defaults to no host logging.</summary>
    public bool IsSystem => Source == AppSource.System;

    // --- Windows components (docs/03 §Windows components) --------------------------------------

    /// <summary>Everything under a Tier-0 root that has no more specific identity.</summary>
    public static AppId Windows { get; } = new("sys:windows");

    /// <summary>`explorer.exe`.</summary>
    public static AppId Explorer { get; } = new("sys:explorer");

    /// <summary>PID 4.</summary>
    public static AppId SystemProcess { get; } = new("sys:system");

    /// <summary>PID 0.</summary>
    public static AppId Idle { get; } = new("sys:idle");

    /// <summary>A `svchost.exe` whose service name could not be determined.</summary>
    public static AppId ServiceGroup { get; } = new("sys:services");

    /// <summary>A specific Windows service host, e.g. <c>sys:service:Dnscache</c>.</summary>
    public static AppId Service(string serviceName) =>
        new("sys:service:" + Require(serviceName, nameof(serviceName)));

    // --- Store / launcher identities -----------------------------------------------------------

    /// <summary>`msix:&lt;PackageFamilyName&gt;`.</summary>
    public static AppId Msix(string packageFamilyName) =>
        new("msix:" + Require(packageFamilyName, nameof(packageFamilyName)));

    /// <summary>`steam:&lt;appid&gt;`.</summary>
    public static AppId Steam(string steamAppId) => new("steam:" + Require(steamAppId, nameof(steamAppId)));

    /// <summary>`epic:&lt;AppName&gt;` — the manifest `AppName`, never the display name.</summary>
    public static AppId Epic(string appName) => new("epic:" + Require(appName, nameof(appName)));

    /// <summary>`gog:&lt;gameId&gt;`.</summary>
    public static AppId Gog(string gameId) => new("gog:" + Require(gameId, nameof(gameId)));

    /// <summary>`itch:&lt;gameId&gt;`.</summary>
    public static AppId Itch(string gameId) => new("itch:" + Require(gameId, nameof(gameId)));

    // --- Catalog, registry, package managers ---------------------------------------------------

    /// <summary>`cat:&lt;rule id&gt;` — catalog ids win over uninstall ids when both match.</summary>
    public static AppId Catalog(string ruleId) => new("cat:" + Require(ruleId, nameof(ruleId)));

    /// <summary>
    /// `uninst:&lt;normalized key name&gt;` — lower-cased, braces stripped, whitespace runs collapsed to a
    /// single hyphen, so `{A1B2}` and `My  App` become stable ids.
    /// </summary>
    public static AppId Uninstall(string uninstallKeyName) =>
        new("uninst:" + NormalizeKeyName(Require(uninstallKeyName, nameof(uninstallKeyName))));

    /// <summary>`scoop:&lt;name&gt;`.</summary>
    public static AppId Scoop(string name) => new("scoop:" + NormalizeKeyName(Require(name, nameof(name))));

    /// <summary>`choco:&lt;name&gt;`.</summary>
    public static AppId Choco(string name) => new("choco:" + NormalizeKeyName(Require(name, nameof(name))));

    /// <summary>`winget:&lt;PackageIdentifier&gt;` — winget ids are case-sensitive and kept verbatim.</summary>
    public static AppId Winget(string packageIdentifier) =>
        new("winget:" + Require(packageIdentifier, nameof(packageIdentifier)));

    // --- Hashed fallbacks ----------------------------------------------------------------------

    /// <summary>
    /// `script:&lt;sha256(lower(canonical script path))[:16]&gt;` — the identity of a script or module
    /// hosted by a runtime such as `python.exe` or `node.exe`.
    /// </summary>
    public static AppId Script(string canonicalScriptPath) =>
        new("script:" + ShortHash(Require(canonicalScriptPath, nameof(canonicalScriptPath))));

    /// <summary>`root:&lt;sha256(lower(canonical install root))[:16]&gt;` — the last-resort identity.</summary>
    public static AppId Root(string canonicalInstallRoot) =>
        new("root:" + ShortHash(Require(canonicalInstallRoot, nameof(canonicalInstallRoot))));

    /// <summary>`user:&lt;guid&gt;` — the target of a user split (docs/03 §User overrides).</summary>
    public static AppId User(Guid id) => new("user:" + id.ToString("n", CultureInfo.InvariantCulture));

    // --- Parsing -------------------------------------------------------------------------------

    /// <summary>Parses a stored id, rejecting anything whose prefix we do not own.</summary>
    public static AppId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"'{value}' is not a valid app id.");

    /// <summary>Parses a stored id, returning false instead of throwing.</summary>
    public static bool TryParse(string? value, out AppId id)
    {
        id = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var i = value.IndexOf(':', StringComparison.Ordinal);
        if (i <= 0 || i == value.Length - 1)
        {
            return false;
        }

        if (AppSourceExtensions.FromPrefix(value[..i]) is null)
        {
            return false;
        }

        id = new AppId(value);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>
    /// Lower-cases, strips braces, and collapses every run of whitespace, underscores and hyphens into a
    /// single hyphen, so cosmetic differences in a registry key do not fork an app's history.
    /// </summary>
    internal static string NormalizeKeyName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var pendingSeparator = false;
        foreach (var ch in raw)
        {
            if (ch is '{' or '}')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch is '_' or '-')
            {
                pendingSeparator = sb.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                sb.Append('-');
                pendingSeparator = false;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    /// <summary>SHA-256 of the lower-cased input, truncated to <see cref="HashLength"/> hex characters.</summary>
    internal static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexStringLower(digest)[..HashLength];
    }

    private static string Require([NotNull] string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An app-id component cannot be empty.", parameterName)
            : value;
}
