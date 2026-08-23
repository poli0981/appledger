using AppLedger.Core.Catalog;
using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Identity;

/// <summary>
/// The S2 gate (docs/20_SPIKES.md): at least 95 % of expected `app_id` matches across the fixture set,
/// and <b>zero</b> games merged into their launcher.
/// </summary>
/// <remarks>
/// The resolver itself lands with the identity milestone (docs/21_ROADMAP.md v0.3), so the scoring test
/// is skipped until then. Everything else in this file runs today and keeps the fixtures honest: they
/// parse, they are internally consistent, and their expectations name ids the scheme can actually produce.
/// A fixture set that rots while nobody is looking is worse than no fixture set.
/// </remarks>
public sealed class IdentityFixtureTests
{
    private static IReadOnlyList<IdentityFixture> Fixtures => IdentityFixture.LoadAll();

    [Fact]
    public void Fixtures_TheTwelveMandatoryScenariosExist() =>
        Fixtures.Count.ShouldBeGreaterThanOrEqualTo(12, "docs/03_APP_IDENTITY.md §Test fixtures lists twelve");

    [Fact]
    public void Fixtures_AllParseAndDeclareAScenario()
    {
        foreach (var fixture in Fixtures)
        {
            fixture.Name.ShouldNotBeNullOrWhiteSpace();
            fixture.Scenario.ShouldNotBeNullOrWhiteSpace($"{fixture.Name} must say what it tests");
            fixture.Processes.ShouldNotBeEmpty(fixture.Name);
        }
    }

    /// <summary>Every expectation must be an id the scheme can produce, or the gate would measure nothing.</summary>
    [Fact]
    public void Fixtures_EveryExpectedIdIsWellFormed()
    {
        foreach (var fixture in Fixtures)
        {
            foreach (var process in fixture.Processes)
            {
                AppId.TryParse(process.Expect, out _)
                    .ShouldBeTrue($"{fixture.Name}: '{process.Expect}' is not a valid app id");
            }
        }
    }

    /// <summary>
    /// Create times must ascend and a parent must precede its child, or the fixture would be asking the
    /// resolver to violate the PID-reuse guard rather than to honour it.
    /// </summary>
    [Fact]
    public void Fixtures_ParentsPrecedeChildren()
    {
        foreach (var fixture in Fixtures)
        {
            var byKey = fixture.Processes.ToDictionary(p => (p.Pid, p.CreateTime));

            foreach (var process in fixture.Processes)
            {
                if (process.ParentPid is not { } parentPid || process.ParentCreateTime is not { } parentCreate)
                {
                    continue;
                }

                // A parent outside the fixture models one that exited before the window; that is legal.
                if (!byKey.ContainsKey((parentPid, parentCreate)))
                {
                    continue;
                }

                parentCreate.ShouldBeLessThan(
                    process.CreateTime,
                    $"{fixture.Name}: pid {process.Pid} claims a parent created at the same time or later");
            }
        }
    }

    /// <summary>
    /// A Tier-2 process is observed without a handle, so a fixture that marks one must not also hand the
    /// resolver a command line or a full image path it could only have got from a handle.
    /// </summary>
    [Fact]
    public void Fixtures_ZeroTouchProcessesCarryNoHandleDerivedFacts()
    {
        foreach (var fixture in Fixtures)
        {
            foreach (var process in fixture.Processes.Where(p => p.ExpectTier == 2))
            {
                process.Cmdline.ShouldBeNull($"{fixture.Name}: pid {process.Pid} is Tier 2 and has no command line");
                process.PackageFamily.ShouldBeNull($"{fixture.Name}: pid {process.Pid} is Tier 2");
            }
        }
    }

    /// <summary>
    /// The rule the whole gate exists for: a game gets its own app, never the launcher's. This is checked
    /// on the fixture expectations themselves, so the intent is enforced even before the resolver exists.
    /// </summary>
    [Fact]
    public void Fixtures_NoGameIsExpectedToResolveIntoItsLauncher()
    {
        var launcherIds = new[] { "cat:steam", "cat:epic", "cat:gog-galaxy", "cat:battlenet", "cat:ea-app", "cat:ubisoft-connect", "cat:itch", "cat:xbox" };

        foreach (var fixture in Fixtures)
        {
            var gameRoots = fixture.Indexes.Steam.Concat(fixture.Indexes.Epic).Concat(fixture.Indexes.Gog)
                .Select(e => e.InstallLocation).ToList();

            foreach (var process in fixture.Processes)
            {
                if (process.Image is null || !gameRoots.Any(root =>
                        process.Image.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                launcherIds.ShouldNotContain(
                    process.Expect,
                    $"{fixture.Name}: pid {process.Pid} lives under a game root but expects the launcher's id");
            }
        }
    }

    /// <summary>Every catalog id a fixture expects must exist in the shipped catalog.</summary>
    [Fact]
    public void Fixtures_ExpectedCatalogIdsExistInTheSeedCatalog()
    {
        var catalog = CatalogParser.Parse(File.ReadAllText(TestPaths.SeedCatalog));
        // Both namespaces mint `cat:<id>`: app rules and anticheat entries (docs/13 §Matching semantics).
        var ids = catalog.Apps.Select(a => a.Id)
            .Concat(catalog.AntiCheat.Select(a => a.Id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var fixture in Fixtures)
        {
            foreach (var process in fixture.Processes.Where(p => p.Expect.StartsWith("cat:", StringComparison.Ordinal)))
            {
                ids.ShouldContain(
                    process.Expect["cat:".Length..],
                    $"{fixture.Name}: expects '{process.Expect}' but the catalog has no such app");
            }
        }
    }

    /// <summary>
    /// The S2 gate itself. Enabled when <c>IdentityResolver</c> exists (docs/21 v0.3); until then the
    /// fixtures above keep the data valid so this becomes a red-to-green step rather than a rewrite.
    /// </summary>
    [Fact(Skip = "S2 gate: enable with the IdentityResolver implementation (docs/21_ROADMAP.md v0.3).")]
    [Trait("Category", "S2")]
    public void Resolve_MatchesAtLeastNinetyFivePercentOfExpectations() =>
        throw new NotImplementedException("IIdentityResolver has no implementation yet.");
}
