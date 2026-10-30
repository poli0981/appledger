using AppLedger.Core.Catalog;
using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Testing;

namespace AppLedger.Core.Tests.Identity;

/// <summary>
/// Turns one S2 fixture into the world a resolver sees: the shipped catalog, the fixture's indexes and
/// overrides, and a policy guard over the same synthetic roots the fixture paths use.
/// </summary>
/// <remarks>
/// The only thing synthesised here is the machine. The catalog is the one that ships, the environment
/// variables are <see cref="EnvExpander.ForValidation"/> — whose values are the <c>C:\Users\fixture</c>
/// profile the fixtures are written against — and the boundary list is the same one
/// <c>InstallRootHeuristicTests</c> asserts against, so an install root this gate computes is one that
/// suite has already pinned.
/// </remarks>
internal static class FixtureEnvironment
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";
    private const string Windows = @"C:\Windows";

    /// <summary>The install-root boundaries of docs/03 §Install root heuristic, as the fixtures see them.</summary>
    private static readonly string[] Boundaries =
    [
        ProgramFiles,
        ProgramFilesX86,
        @"C:\Users\fixture\AppData\Local\Programs",
        @"C:\Users\fixture\AppData\Local",
        @"C:\Users\fixture\AppData\Roaming",
        @"C:\Users\fixture",
        @"C:\ProgramData",
        Windows,
    ];

    /// <summary>The shipped catalog, parsed once for the whole fixture set.</summary>
    public static CatalogDocument Catalog { get; } =
        CatalogParser.Parse(File.ReadAllText(TestPaths.SeedCatalog));

    /// <summary>The policy guard, which decides Tier 0 and the zero-touch tier the fixtures assert.</summary>
    public static IPolicyGuard Policy { get; } = new FixturePolicy(Catalog);

    /// <summary>A resolver over one fixture's synthetic machine.</summary>
    public static IdentityResolver ResolverFor(IdentityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var context = new IdentityContext(IndexesOf(fixture), OverridesOf(fixture));

        return new IdentityResolver(
            Catalog,
            EnvExpander.ForValidation,
            Policy,
            new InstallRootHeuristic(Boundaries),
            () => context);
    }

    /// <summary>
    /// One fixture process as the facts the resolver is allowed to know. A Tier-2 entry keeps its image
    /// path — that comes from ETW, not from a handle — but its command line and package name stay null,
    /// which the fixture schema already enforces.
    /// </summary>
    public static ProcessFacts FactsOf(FixtureProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return new ProcessFacts
        {
            Key = new ProcessKey(process.Pid, process.CreateTime),
            ImageFileName = process.ImageName,
            ImagePath = process.Image,
            CommandLine = process.Cmdline,
            Signer = process.Signer,
            PackageFamilyName = process.PackageFamily,
            ProductName = process.ProductName,
            CompanyName = process.CompanyName,
            Parent = process.ParentPid is { } pid && process.ParentCreateTime is { } created
                ? new ProcessKey(pid, created)
                : null,
            Tier = Policy.TierOfProcess(process.Image, process.ImageName),
        };
    }

    /// <summary>
    /// Every process of one fixture, resolved in order. Order is not a convenience: adoption reads the
    /// parent's answer out of the resolver's cache, so resolving a child before its parent would be asking
    /// a different question than the fixture poses.
    /// </summary>
    public static IReadOnlyList<(FixtureProcess Process, ResolutionResult Result)> ResolveAll(
        IdentityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var resolver = ResolverFor(fixture);

        return
        [
            .. fixture.Processes
                .OrderBy(p => p.CreateTime)
                .Select(p => (p, resolver.Resolve(FactsOf(p)))),
        ];
    }

    /// <summary>
    /// The same fixture resolved the way the shipping Agent resolves today: catalog and host rules, package
    /// identity from the process itself, adoption and the install-root fallback — but no indexes, because
    /// the adapters that build them are the next slice (docs/03 §What ships).
    /// </summary>
    public static IReadOnlyList<(FixtureProcess Process, ResolutionResult Result)> ResolveAllWithEmptyIndexes(
        IdentityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var context = new IdentityContext(IdentityIndexes.Empty, OverridesOf(fixture));
        var resolver = new IdentityResolver(
            Catalog,
            EnvExpander.ForValidation,
            Policy,
            new InstallRootHeuristic(Boundaries),
            () => context);

        return
        [
            .. fixture.Processes
                .OrderBy(p => p.CreateTime)
                .Select(p => (p, resolver.Resolve(FactsOf(p)))),
        ];
    }

    /// <summary>The <c>cat:</c> ids of every app the catalog calls a launcher.</summary>
    public static IReadOnlySet<string> LauncherAppIds { get; } =
        Catalog.Launchers.Select(id => "cat:" + id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every directory a fixture's launcher manifests claim, which is to say every game root.</summary>
    public static IReadOnlyList<string> GameRootsOf(IdentityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return
        [
            .. fixture.Indexes.Steam
                .Concat(fixture.Indexes.Epic)
                .Concat(fixture.Indexes.Gog)
                .Concat(fixture.Indexes.Itch)
                .Select(e => e.InstallLocation),
        ];
    }

    /// <summary>
    /// The same fixture resolved from the facts the <i>collector</i> supplies, which are not the facts these
    /// fixtures carry. <c>InstanceRegistry</c> builds a <see cref="ProcessFacts"/> from the process table and
    /// one <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle: key, image name, image path, command line,
    /// package family, parent and tier — and **no signer and no PE metadata**, because docs/03 §Metadata
    /// enrichment reads those once per <c>(app_id, version)</c>, which is after resolution rather than
    /// before it.
    /// </summary>
    /// <remarks>
    /// This exists so a claim about the shipping Agent is asserted rather than believed. Most catalog app
    /// rules name a signer, and AND-across-kinds means a rule that names one cannot match without it — so
    /// the Agent reaches a different, lower-precedence identity than the gate does, and saying which is the
    /// honest way to describe what it does (docs/24_ADR.md §Findings).
    /// </remarks>
    public static IReadOnlyList<(FixtureProcess Process, ResolutionResult Result)> ResolveAllAsTheCollectorWould(
        IdentityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var resolver = ResolverFor(fixture);

        return
        [
            .. fixture.Processes
                .OrderBy(p => p.CreateTime)
                .Select(p => (p, resolver.Resolve(FactsOf(p) with
                {
                    Signer = null,
                    ProductName = null,
                    CompanyName = null,
                    FileDescription = null,
                }))),
        ];
    }

    private static IdentityIndexes IndexesOf(IdentityFixture fixture) => new()
    {
        Uninstall =
        [
            .. fixture.Indexes.Uninstall.Select(e =>
                new UninstallIndexEntry(e.Key, e.DisplayName, e.InstallLocation, e.DisplayIcon, e.Publisher)),
        ],
        Msix = fixture.Indexes.Msix.ToHashSet(StringComparer.OrdinalIgnoreCase),
        Steam = Entries(fixture.Indexes.Steam),
        Epic = Entries(fixture.Indexes.Epic),
        Gog = Entries(fixture.Indexes.Gog),
        Itch = Entries(fixture.Indexes.Itch),
        Scoop = Entries(fixture.Indexes.Scoop),
        Choco = Entries(fixture.Indexes.Choco),
        LoadedDrivers = fixture.Indexes.LoadedDrivers.ToHashSet(StringComparer.OrdinalIgnoreCase),
        Services = fixture.Indexes.Services.ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    private static IReadOnlyList<LauncherIndexEntry> Entries(IReadOnlyList<FixtureLauncherEntry> entries) =>
        [.. entries.Select(e => new LauncherIndexEntry(e.Id, e.InstallLocation, e.Name))];

    private static IReadOnlyList<IdentityOverride> OverridesOf(IdentityFixture fixture) =>
    [
        .. fixture.Overrides.Select(o => new IdentityOverride(
            string.Equals(o.Kind, "split", StringComparison.OrdinalIgnoreCase)
                ? OverrideKind.Split
                : OverrideKind.Merge,
            o.Match,
            AppId.Parse(o.Value))),
    ];

    /// <summary>
    /// The real tier tables over the fixture's roots. Not a stub: Tier 0 and the zero-touch tier are the
    /// two policy answers the gate leans on, and a stub would let a wrong one pass.
    /// </summary>
    private sealed class FixturePolicy : IPolicyGuard
    {
        private readonly PathTierTable _paths;
        private readonly ProcessTierTable _processes;

        public FixturePolicy(CatalogDocument catalog)
        {
            _paths = new PathTierTable(
                [Windows, Path.Combine(ProgramFiles, "WindowsApps"), Path.Combine(ProgramFilesX86, "WindowsApps")],
                sensitiveRoots: [],
                sensitiveGlobs: []);

            _processes = new ProcessTierTable(
                catalog.ProtectedProcesses,
                [.. catalog.AntiCheat.SelectMany(a => a.Dirs)]);
        }

        public PathDecision Evaluate(string? rawPath) =>
            throw new NotSupportedException("The identity fixtures only ever hand over canonical paths.");

        public PathTier TierOf(string canonicalPath) => _paths.Classify(canonicalPath, out _);

        public bool CanScan(string canonicalPath) => _paths.CanScan(canonicalPath);

        public ProcessTier TierOfProcess(string? canonicalImagePath, string? imageFileName) =>
            _processes.Classify(canonicalImagePath, imageFileName);

        public bool IsInsideDataRoot(string canonicalPath) => false;
    }
}
