using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Identity;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Infrastructure.Tests.Identity;

/// <summary>
/// The identity indexes, read off the machine the tests run on (docs/03_APP_IDENTITY.md steps 4, 6 and 7).
/// </summary>
/// <remarks>
/// These are smoke tests in the sense docs/19_TESTING.md means: they prove the adapter talks to Windows and
/// obeys path policy, not that a particular game is installed. Several assertions are therefore over
/// "every entry returned" and go vacuous on a machine without Steam or Scoop — so each test writes what it
/// found to test output. A reader can then tell a real pass from an empty one, which is the whole reason a
/// vacuous assertion is acceptable here at all.
/// <para>
/// The Uninstall registry is the exception: every Windows machine has one, so that test is never vacuous.
/// </para>
/// </remarks>
public sealed class IdentityIndexSourceTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>xUnit supplies the sink; the counts are written so a vacuous pass is visible.</summary>
    public IdentityIndexSourceTests(ITestOutputHelper output) => _output = output;

    private static PolicyGuard Policy { get; } =
        PolicyGuard.Create(catalog: null, dataRoot: null, folders: KnownFolders.Current);

    /// <summary>
    /// The Uninstall keys, from all three views. Never vacuous, and every path it reports has to be
    /// canonical and outside Tier 0 — an un-canonicalized value would never match an image path, and a
    /// Tier-0 one is a Windows component rather than an app (docs/03 §Windows components).
    /// </summary>
    [Fact]
    public void UninstallRegistryReader_ReportsCanonicalPathsOutsideTierZero()
    {
        var entries = new UninstallRegistryReader(Policy).Read();

        _output.WriteLine($"uninstall entries: {entries.Count}");
        entries.ShouldNotBeEmpty("every Windows machine has Uninstall keys with install locations");

        entries.Select(e => e.Key).ShouldBeUnique();

        foreach (var entry in entries)
        {
            entry.Key.ShouldNotBeNullOrWhiteSpace();
            (entry.InstallLocation ?? entry.DisplayIcon).ShouldNotBeNull(
                $"{entry.Key} was kept with no path at all, so it can never claim an image");

            Rooted(entry.InstallLocation, entry.Key);
            Rooted(entry.DisplayIcon, entry.Key);
        }
    }

    /// <summary>
    /// A Steam entry claims the game's own directory under <c>steamapps\common</c>, never the library root
    /// that contains it. <see cref="IdentityIndexes.ClaimOf"/> takes the longest claim, so a library root in
    /// the index would swallow every game installed in it.
    /// </summary>
    [Fact]
    public void LauncherManifestReader_SteamEntriesClaimTheGameDirectoryNotTheLibrary()
    {
        var entries = new LauncherManifestReader(Policy, KnownFolders.Current).ReadSteam();

        _output.WriteLine($"steam entries: {entries.Count}");

        foreach (var entry in entries)
        {
            _output.WriteLine($"  {entry.Id} -> {entry.InstallLocation}");
            entry.Id.ShouldNotBeNullOrWhiteSpace();
            Rooted(entry.InstallLocation, entry.Id);

            entry.InstallLocation.ShouldContain(
                @"steamapps\common",
                Case.Insensitive,
                $"steam:{entry.Id} claims a directory that is not a game directory");
        }
    }

    /// <summary>The other three launchers: whatever they report must be usable, and none may throw.</summary>
    [Fact]
    public void LauncherManifestReader_EpicGogAndItchReportUsableEntries()
    {
        var reader = new LauncherManifestReader(Policy, KnownFolders.Current);

        foreach (var (name, entries) in new (string, IReadOnlyList<LauncherIndexEntry>)[]
        {
            ("epic", reader.ReadEpic()),
            ("gog", reader.ReadGog()),
            ("itch", reader.ReadItch()),
        })
        {
            _output.WriteLine($"{name} entries: {entries.Count}");

            foreach (var entry in entries)
            {
                entry.Id.ShouldNotBeNullOrWhiteSpace();
                Rooted(entry.InstallLocation, $"{name}:{entry.Id}");
            }
        }
    }

    /// <summary>Scoop and Chocolatey, read from their directory layout rather than from the tools.</summary>
    [Fact]
    public void PackageManagerReader_ReportsOneEntryPerPackageDirectory()
    {
        var reader = new PackageManagerReader(Policy, KnownFolders.Current);
        var scoop = reader.ReadScoop();
        var choco = reader.ReadChocolatey();

        _output.WriteLine($"scoop: {scoop.Count}, chocolatey: {choco.Count}");

        foreach (var entry in scoop.Concat(choco))
        {
            entry.Id.ShouldNotBeNullOrWhiteSpace();
            // The package name is the directory name: that is the whole layout both managers use.
            entry.Id.ShouldBe(Path.GetFileName(entry.InstallLocation), StringCompareShould.IgnoreCase);
            Rooted(entry.InstallLocation, entry.Id);
        }

        scoop.Select(e => e.Id).ShouldBeUnique();
    }

    /// <summary>
    /// A rebuild is rate-limited, because <see cref="IIdentityIndexSource.Refresh"/> is called from the
    /// collector's tick loop. Re-reading the registry every tick would be a budget problem rather than a
    /// correctness one, which is exactly the kind that ships (docs/05_COLLECTOR.md §Budget).
    /// </summary>
    [Fact]
    public void Refresh_BeforeTheIntervalElapses_DoesNothing()
    {
        var clock = new StepClock();
        var source = new IdentityIndexSource(
            Policy, KnownFolders.Current, clock, TimeSpan.FromMinutes(10));

        source.BuildNow();
        var first = source.Current;

        source.Refresh().ShouldBeFalse("nothing is due one second after a build");
        source.Current.ShouldBeSameAs(first, "the snapshot must not even be replaced by an equal one");

        clock.Advance(TimeSpan.FromMinutes(11));

        // The machine has not changed between the two reads, so the rebuild happens and reports no change -
        // which is the point: a caller that invalidated on every rebuild would do so for nothing.
        source.Refresh().ShouldBeFalse("the rebuild found the same machine");
    }

    /// <summary>
    /// <see cref="IdentityIndexSource.BuildNow"/> ignores the interval, because the Agent calls it once
    /// before the collector starts so the first process it resolves already sees a full index.
    /// </summary>
    [Fact]
    public void BuildNow_IgnoresTheInterval()
    {
        var clock = new StepClock();
        var source = new IdentityIndexSource(Policy, KnownFolders.Current, clock, TimeSpan.FromHours(1));

        source.Current.ShouldBeSameAs(IdentityIndexes.Empty, "nothing has been read yet");

        var built = source.BuildNow();

        built.Uninstall.ShouldNotBeEmpty();
        source.Current.ShouldBeSameAs(built);
    }

    private static void Rooted(string? path, string owner)
    {
        if (path is null)
        {
            return;
        }

        Path.IsPathFullyQualified(path).ShouldBeTrue($"{owner}: '{path}' is not a rooted path");
        Policy.TierOf(path).ShouldNotBe(PathTier.ProtectedOs, $"{owner}: '{path}' is Tier 0");
    }

    /// <summary>A clock that only moves when a test says so.</summary>
    private sealed class StepClock : IClock
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _now;

        public TimeSpan Elapsed { get; private set; }

        public void Advance(TimeSpan by)
        {
            _now = _now.Add(by);
            Elapsed += by;
        }
    }
}
