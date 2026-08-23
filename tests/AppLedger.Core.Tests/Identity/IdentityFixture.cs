using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppLedger.Core.Tests.Identity;

/// <summary>
/// The synthetic environment one S2 fixture describes: the indexes the resolver would have built from the
/// registry, the store and the launcher manifests, plus the process table it must classify.
/// </summary>
/// <remarks>
/// The fixtures are authored ahead of the resolver on purpose. S2 is a go/no-go gate (docs/20_SPIKES.md),
/// and a gate whose test data is written after the implementation measures the implementation against
/// itself. Every grouping bug fixed later adds a fixture here first, red before green.
/// </remarks>
public sealed record IdentityFixture
{
    /// <summary>File name without extension, e.g. <c>01_chrome</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What this scenario is testing, in one sentence.</summary>
    public required string Scenario { get; init; }

    /// <summary>The synthetic indexes available to the resolver.</summary>
    public FixtureIndexes Indexes { get; init; } = new();

    /// <summary>The process table, in ascending create-time order.</summary>
    public required IReadOnlyList<FixtureProcess> Processes { get; init; }

    /// <summary>User overrides applied before resolution.</summary>
    public IReadOnlyList<FixtureOverride> Overrides { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads every fixture, ordered by name so failures are reported in a stable order.</summary>
    public static IReadOnlyList<IdentityFixture> LoadAll()
    {
        var dir = TestPaths.Fixture("Identity", "fixtures");
        var fixtures = new List<IdentityFixture>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            fixtures.Add(Load(file));
        }

        return fixtures;
    }

    /// <summary>Loads one fixture file, failing loudly on an unknown field.</summary>
    public static IdentityFixture Load(string path) =>
        JsonSerializer.Deserialize<IdentityFixture>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"Fixture '{path}' is empty.");
}

/// <summary>The indexes a resolver consults, as fixture data rather than as live registry and disk reads.</summary>
public sealed record FixtureIndexes
{
    /// <summary>Uninstall registry entries.</summary>
    public IReadOnlyList<FixtureUninstallEntry> Uninstall { get; init; } = [];

    /// <summary>Installed MSIX packages, by package family name.</summary>
    public IReadOnlyList<string> Msix { get; init; } = [];

    /// <summary>Steam applications, keyed by install directory.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Steam { get; init; } = [];

    /// <summary>Epic applications.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Epic { get; init; } = [];

    /// <summary>GOG applications.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Gog { get; init; } = [];

    /// <summary>itch.io applications.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Itch { get; init; } = [];

    /// <summary>Scoop app directories, by app name.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Scoop { get; init; } = [];

    /// <summary>Chocolatey lib directories, by package name.</summary>
    public IReadOnlyList<FixtureLauncherEntry> Choco { get; init; } = [];

    /// <summary>Anti-cheat drivers seen through ETW ImageLoad during this scenario.</summary>
    public IReadOnlyList<string> LoadedDrivers { get; init; } = [];

    /// <summary>Running Windows services, for the anti-cheat and svchost cases.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];
}

/// <summary>One Uninstall registry entry.</summary>
public sealed record FixtureUninstallEntry
{
    /// <summary>The registry key name.</summary>
    public required string Key { get; init; }

    /// <summary>The <c>DisplayName</c> value.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The <c>InstallLocation</c> value.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>The <c>DisplayIcon</c> value.</summary>
    public string? DisplayIcon { get; init; }

    /// <summary>The <c>Publisher</c> value.</summary>
    public string? Publisher { get; init; }
}

/// <summary>One entry in a launcher or package-manager index.</summary>
public sealed record FixtureLauncherEntry
{
    /// <summary>The launcher's own identifier, e.g. a Steam appid or an Epic AppName.</summary>
    public required string Id { get; init; }

    /// <summary>The install directory the launcher claims.</summary>
    public required string InstallLocation { get; init; }

    /// <summary>Display name, when the manifest carries one.</summary>
    public string? Name { get; init; }
}

/// <summary>One process the resolver must classify.</summary>
public sealed record FixtureProcess
{
    /// <summary>Process id.</summary>
    public required int Pid { get; init; }

    /// <summary>Create time, as an arbitrary monotonically increasing fixture value.</summary>
    public required long CreateTime { get; init; }

    /// <summary>Full image path. Null models a Tier-2 process, where we never open a handle.</summary>
    public string? Image { get; init; }

    /// <summary>Image file name. Always known, because it comes from ETW.</summary>
    public required string ImageName { get; init; }

    /// <summary>Command line, or null when policy or the tier forbids reading it.</summary>
    public string? Cmdline { get; init; }

    /// <summary>Authenticode signer subject.</summary>
    public string? Signer { get; init; }

    /// <summary>MSIX package family name.</summary>
    public string? PackageFamily { get; init; }

    /// <summary>Parent process id.</summary>
    public int? ParentPid { get; init; }

    /// <summary>Parent create time, used for the PID-reuse guard.</summary>
    public long? ParentCreateTime { get; init; }

    /// <summary>PE <c>ProductName</c>.</summary>
    public string? ProductName { get; init; }

    /// <summary>PE <c>CompanyName</c>.</summary>
    public string? CompanyName { get; init; }

    /// <summary>The `app_id` a correct resolver must produce.</summary>
    public required string Expect { get; init; }

    /// <summary>Expected process tier, when the scenario is about zero-touch handling.</summary>
    public int? ExpectTier { get; init; }

    /// <summary>Expected confidence, when the scenario is about the confidence table.</summary>
    public double? ExpectConfidence { get; init; }

    /// <summary>Why this expectation is what it is. Read by humans, not by the runner.</summary>
    public string? Why { get; init; }
}

/// <summary>A user override applied before resolution.</summary>
public sealed record FixtureOverride
{
    /// <summary>Override kind: <c>merge</c> or <c>split</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What the override matches: an image path, an install root, or a command-line substring.</summary>
    public required string Match { get; init; }

    /// <summary>The app id the match is forced to.</summary>
    public required string Value { get; init; }
}
