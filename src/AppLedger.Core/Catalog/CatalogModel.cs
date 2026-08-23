using System.Text.Json.Serialization;

namespace AppLedger.Core.Catalog;

/// <summary>The host-rule kinds of docs/03_APP_IDENTITY.md §Host rules. Unknown kinds reject the file.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<HostRuleKind>))]
public enum HostRuleKind
{
    /// <summary>A hard-coded identity for a specific PID or executable, e.g. `explorer.exe` to `sys:explorer`.</summary>
    Fixed,

    /// <summary>A Windows component that resolves to `sys:windows`.</summary>
    System,

    /// <summary>`svchost.exe`: `sys:service:&lt;name&gt;` when `-s` is present, `sys:services` otherwise.</summary>
    ServiceGroup,

    /// <summary>`rundll32`/`regsvr32`/`dllhost`: the app owning the DLL argument, else `sys:windows`.</summary>
    DllArgOrSystem,

    /// <summary>Helper processes that take their parent's identity.</summary>
    AttachParent,

    /// <summary>A runtime host whose first script argument becomes a `script:` identity.</summary>
    ScriptFromCmdline,

    /// <summary>An anti-cheat helper; always Tier 2.</summary>
    AnticheatHelper,

    /// <summary>Children of a Launcher-category app, except those under a game's own root.</summary>
    LauncherChildren,
}

/// <summary>How much of a host's traffic is recorded (docs/12_PRIVACY_AND_RETENTION.md §Defaults).</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<HostLogging>))]
public enum HostLogging
{
    /// <summary>Byte totals only, no host names. The default for Browser and `sys:*`.</summary>
    None,

    /// <summary>The registrable domain (eTLD+1). The default for everything else.</summary>
    Etld1,

    /// <summary>The full host name. Only ever set by the user, per app.</summary>
    Full,
}

/// <summary>How confidently an anti-cheat entry can be recognised on a real system.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<AntiCheatMatchConfidence>))]
public enum AntiCheatMatchConfidence
{
    /// <summary>Recognised by a kernel driver file name.</summary>
    Driver,

    /// <summary>Recognised by a Windows service name.</summary>
    Service,

    /// <summary>Recognised only by a directory name; weakest, and marked as such in the catalog.</summary>
    Dir,

    /// <summary>No reliable signal (in-process protections such as VAC).</summary>
    None,
}

/// <summary>The signals that identify an app. See <see cref="CatalogValidator"/> for how they combine.</summary>
public sealed record CatalogMatch
{
    /// <summary>Authenticode subject common names, matched exactly and case-insensitively.</summary>
    public IReadOnlyList<string> Signer { get; init; } = [];

    /// <summary>Executable file names, without a path.</summary>
    public IReadOnlyList<string> Exe { get; init; } = [];

    /// <summary>Install-root globs (docs/13 §Glob grammar).</summary>
    public IReadOnlyList<string> InstallRootGlob { get; init; } = [];

    /// <summary>MSIX package family names. A strong signal on its own.</summary>
    public IReadOnlyList<string> PackageFamily { get; init; } = [];
}

/// <summary>Child processes that belong to an app when they are found under its root.</summary>
public sealed record CatalogHelpers
{
    /// <summary>Helper executable names.</summary>
    public IReadOnlyList<string> Exe { get; init; } = [];

    /// <summary>Command-line substrings that mark a helper, e.g. <c>--type=</c>.</summary>
    public IReadOnlyList<string> CmdlineContains { get; init; } = [];
}

/// <summary>One application rule. <c>id</c> becomes the `cat:&lt;id&gt;` app id and never changes once released.</summary>
public sealed record CatalogApp
{
    /// <summary>Lower-case kebab id, unique within the catalog.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown when no better metadata is available.</summary>
    public required string Name { get; init; }

    /// <summary>Publisher, for the identity card.</summary>
    public string? Publisher { get; init; }

    /// <summary>Category from the catalog taxonomy.</summary>
    public required string Category { get; init; }

    /// <summary>The identifying signals.</summary>
    public required CatalogMatch Match { get; init; }

    /// <summary>Helper processes adopted into this app.</summary>
    public CatalogHelpers? Helpers { get; init; }

    /// <summary>Known data directories, as globs.</summary>
    public IReadOnlyList<string> DataDirs { get; init; } = [];

    /// <summary>Known reclaimable cache directories, as globs. Labelled only; never deleted by us.</summary>
    public IReadOnlyList<string> CacheDirs { get; init; } = [];

    /// <summary>Overrides the category's default host-logging level.</summary>
    public HostLogging? HostLoggingDefault { get; init; }

    /// <summary>Free-text note for maintainers. Never shown in the UI.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// One ordered host rule. <see cref="Exe"/>, <see cref="ExeGlob"/> and <see cref="CmdlineContains"/> are
/// OR-ed with each other and within each list (docs/13 §Matching semantics).
/// </summary>
public sealed record CatalogHostRule
{
    /// <summary>Which rule this is.</summary>
    public required HostRuleKind Rule { get; init; }

    /// <summary>Executable names.</summary>
    public IReadOnlyList<string> Exe { get; init; } = [];

    /// <summary>Executable-name globs.</summary>
    public IReadOnlyList<string> ExeGlob { get; init; } = [];

    /// <summary>Command-line substrings.</summary>
    public IReadOnlyList<string> CmdlineContains { get; init; } = [];

    /// <summary>A specific PID. Only meaningful for <see cref="HostRuleKind.Fixed"/>.</summary>
    public int? Pid { get; init; }

    /// <summary>The `sys:*` id a <see cref="HostRuleKind.Fixed"/> rule assigns.</summary>
    public string? AppId { get; init; }
}

/// <summary>One anti-cheat family. Any match promotes the affected app to <c>ProcessTier.ZeroTouch</c>.</summary>
public sealed record CatalogAntiCheat
{
    /// <summary>Lower-case kebab id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Windows service names.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>Kernel driver file names, as seen through ETW ImageLoad.</summary>
    public IReadOnlyList<string> Drivers { get; init; } = [];

    /// <summary>Directory names found inside a game root.</summary>
    public IReadOnlyList<string> Dirs { get; init; } = [];

    /// <summary>How reliable the signals above are.</summary>
    public required AntiCheatMatchConfidence MatchConfidence { get; init; }

    /// <summary>Free-text note for maintainers.</summary>
    public string? Notes { get; init; }
}

/// <summary>A Tier-1 location: its size is counted, its file names never stored.</summary>
public sealed record CatalogSensitivePath
{
    /// <summary>The glob covering the location.</summary>
    public required string Glob { get; init; }

    /// <summary>The generic class reported instead of a path, e.g. <c>password-vault</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// The whole signed rules file (docs/13_CATALOG_RULES.md §Schema). Parsed strictly: an unknown field
/// rejects the document, because a silent typo in identity rules is worse than a failed update.
/// </summary>
public sealed record CatalogDocument
{
    /// <summary>Schema version. Must equal <see cref="CatalogParser.SupportedSchema"/>.</summary>
    public required int Schema { get; init; }

    /// <summary>CalVer <c>YYYY.MM.N</c>; an older version is never loaded over a newer one.</summary>
    public required string Version { get; init; }

    /// <summary>When the file was generated.</summary>
    public required DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>Minimum app version that understands this file.</summary>
    public required string MinAppVersion { get; init; }

    /// <summary>The category taxonomy. Must be a superset of the built-in one.</summary>
    public required IReadOnlyList<string> Categories { get; init; }

    /// <summary>Application rules.</summary>
    public required IReadOnlyList<CatalogApp> Apps { get; init; }

    /// <summary>Ordered host rules.</summary>
    public required IReadOnlyList<CatalogHostRule> HostRules { get; init; }

    /// <summary>Ids of the apps that count as launchers for the <c>launcher_children</c> rule.</summary>
    public IReadOnlyList<string> Launchers { get; init; } = [];

    /// <summary>Anti-cheat families.</summary>
    [JsonPropertyName("anticheat")]
    public IReadOnlyList<CatalogAntiCheat> AntiCheat { get; init; } = [];

    /// <summary>Extensions to the built-in Tier-0 minimum. The built-ins can be extended, never removed.</summary>
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];

    /// <summary>Tier-1 locations.</summary>
    public IReadOnlyList<CatalogSensitivePath> SensitivePaths { get; init; } = [];

    /// <summary>Extensions to the built-in PPL process list.</summary>
    public IReadOnlyList<string> ProtectedProcesses { get; init; } = [];

    /// <summary>Adapter description substrings that mark a tunnel/VPN interface.</summary>
    public IReadOnlyList<string> TunnelAdapterNames { get; init; } = [];

    /// <summary>Service name to friendly name, for the `sys:service:*` display names.</summary>
    public IReadOnlyDictionary<string, string> SystemDisplayNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
