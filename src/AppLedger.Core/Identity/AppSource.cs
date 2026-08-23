namespace AppLedger.Core.Identity;

/// <summary>
/// Where an <see cref="AppId"/> came from. The order of the members is the precedence order of
/// docs/03_APP_IDENTITY.md: when several sources match the same process, the lowest value wins.
/// </summary>
/// <remarks>
/// Do not renumber: <see cref="AppSourceExtensions.Wins"/> and the resolver both rely on the ordering,
/// and <see cref="AppSourceExtensions.ToPrefix"/> pins the stored string form of each source.
/// </remarks>
public enum AppSource
{
    /// <summary>A user override (`user:` or a forced merge target). Always wins.</summary>
    User = 0,

    /// <summary>A Windows component resolved from a Tier-0 image path, before any handle is opened.</summary>
    System = 1,

    /// <summary>A catalog rule (`cat:`).</summary>
    Catalog = 2,

    /// <summary>An MSIX/AppX package family name (`msix:`).</summary>
    Msix = 3,

    /// <summary>A Steam application id from a launcher manifest (`steam:`).</summary>
    Steam = 4,

    /// <summary>An Epic Games `AppName` from a `.item` manifest (`epic:`).</summary>
    Epic = 5,

    /// <summary>A GOG game id from `goggame-*.info` (`gog:`).</summary>
    Gog = 6,

    /// <summary>An itch.io game id from `.itch/receipt.json.gz` (`itch:`).</summary>
    Itch = 7,

    /// <summary>An Uninstall registry key (`uninst:`).</summary>
    Uninstall = 8,

    /// <summary>A Scoop app directory (`scoop:`).</summary>
    Scoop = 9,

    /// <summary>A Chocolatey lib directory (`choco:`).</summary>
    Choco = 10,

    /// <summary>A winget package identifier (`winget:`).</summary>
    Winget = 11,

    /// <summary>A script or module hosted by a runtime (`script:`).</summary>
    Script = 12,

    /// <summary>The install-root fallback (`root:`).</summary>
    Root = 13,
}

/// <summary>Precedence and prefix helpers for <see cref="AppSource"/>.</summary>
public static class AppSourceExtensions
{
    /// <summary>True when <paramref name="candidate"/> outranks <paramref name="incumbent"/>.</summary>
    public static bool Wins(this AppSource candidate, AppSource incumbent) => candidate < incumbent;

    /// <summary>The `app_id` prefix this source produces, without the colon.</summary>
    public static string ToPrefix(this AppSource source) => source switch
    {
        AppSource.User => "user",
        AppSource.System => "sys",
        AppSource.Catalog => "cat",
        AppSource.Msix => "msix",
        AppSource.Steam => "steam",
        AppSource.Epic => "epic",
        AppSource.Gog => "gog",
        AppSource.Itch => "itch",
        AppSource.Uninstall => "uninst",
        AppSource.Scoop => "scoop",
        AppSource.Choco => "choco",
        AppSource.Winget => "winget",
        AppSource.Script => "script",
        AppSource.Root => "root",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown app source."),
    };

    /// <summary>The source that owns a given `app_id` prefix, or null when the prefix is unknown.</summary>
    public static AppSource? FromPrefix(string prefix) => prefix switch
    {
        "user" => AppSource.User,
        "sys" => AppSource.System,
        "cat" => AppSource.Catalog,
        "msix" => AppSource.Msix,
        "steam" => AppSource.Steam,
        "epic" => AppSource.Epic,
        "gog" => AppSource.Gog,
        "itch" => AppSource.Itch,
        "uninst" => AppSource.Uninstall,
        "scoop" => AppSource.Scoop,
        "choco" => AppSource.Choco,
        "winget" => AppSource.Winget,
        "script" => AppSource.Script,
        "root" => AppSource.Root,
        _ => null,
    };
}
