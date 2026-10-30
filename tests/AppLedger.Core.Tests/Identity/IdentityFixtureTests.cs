using AppLedger.Core.Catalog;
using AppLedger.Core.Identity;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

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
    private readonly ITestOutputHelper _output;

    /// <summary>xUnit supplies the output sink; the S2 score is written to it on success too.</summary>
    public IdentityFixtureTests(ITestOutputHelper output) => _output = output;

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
                Should.NotThrow(
                    () => process.ExpectedId(),
                    $"{fixture.Name}: '{process.Expect}' is not an id the scheme can mint");
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
    /// The S2 gate itself (docs/20_SPIKES.md §S2): at least 95 % of the expected ids across the whole
    /// fixture set.
    /// </summary>
    /// <remarks>
    /// The failure message lists every miss rather than only the percentage. A gate that says "86.5 %" and
    /// stops has told the next person nothing about which scenario regressed, and docs/20 answers a failure
    /// with "the resolver is redesigned" — which needs the cases, not the score.
    /// </remarks>
    [Fact]
    [Trait("Category", "S2")]
    public void Resolve_MatchesAtLeastNinetyFivePercentOfExpectations()
    {
        var total = 0;
        var matched = 0;
        var misses = new List<string>();

        foreach (var fixture in Fixtures)
        {
            foreach (var (process, result) in FixtureEnvironment.ResolveAll(fixture))
            {
                var expected = process.ExpectedId();
                total++;

                if (result.AppId == expected)
                {
                    matched++;
                    continue;
                }

                misses.Add(
                    $"  {fixture.Name} pid {process.Pid} ({process.ImageName}): "
                    + $"expected {expected.Value}, got {result.AppId.Value} via {result.DecidedBy}");
            }
        }

        var rate = total == 0 ? 0 : 100.0 * matched / total;

        // Written on success as well as on failure: docs/20_SPIKES.md records S2 as a number, and a gate
        // that only speaks up when it fails leaves nowhere to read that number from.
        _output.WriteLine($"S2: {matched}/{total} expectations matched = {rate:N1} %");
        foreach (var miss in misses)
        {
            _output.WriteLine(miss);
        }

        rate.ShouldBeGreaterThanOrEqualTo(
            95.0,
            $"S2 scored {matched}/{total} = {rate:N1} %{Environment.NewLine}"
            + string.Join(Environment.NewLine, misses));
    }

    /// <summary>
    /// The other half of the S2 pass criterion, and the one that is not a percentage: <b>zero</b> games
    /// merged into their launcher. Checked on what the resolver produced, not on what the fixtures expect —
    /// <see cref="Fixtures_NoGameIsExpectedToResolveIntoItsLauncher"/> already guards the expectations.
    /// </summary>
    [Fact]
    [Trait("Category", "S2")]
    public void Resolve_NeverMergesAGameIntoItsLauncher()
    {
        foreach (var fixture in Fixtures)
        {
            var gameRoots = FixtureEnvironment.GameRootsOf(fixture);
            if (gameRoots.Count == 0)
            {
                continue;
            }

            // Both fidelities. With the manifest index a game has an identity of its own and never reaches
            // adoption at all, so checking only that configuration would have proved nothing about the one
            // the Agent actually ships with: resolved with empty indexes, the game was adopted into
            // `cat:steam` because it lives inside the Steam folder (docs/24_ADR.md §Findings).
            Check(FixtureEnvironment.ResolveAll(fixture), "with indexes");
            Check(FixtureEnvironment.ResolveAllWithEmptyIndexes(fixture), "with empty indexes");

            void Check(
                IReadOnlyList<(FixtureProcess Process, ResolutionResult Result)> resolved, string fidelity)
            {
                foreach (var (process, result) in resolved)
                {
                    if (process.Image is null
                        || !gameRoots.Any(r => process.Image.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    FixtureEnvironment.LauncherAppIds.ShouldNotContain(
                        result.AppId.Value,
                        $"{fixture.Name} {fidelity}: pid {process.Pid} ({process.ImageName}) lives under a "
                        + $"game root and was merged into {result.AppId.Value}");
                }
            }
        }
    }

    /// <summary>
    /// The confidence table of docs/03 §Confidence, where a fixture makes it the point. These numbers are
    /// user-visible: below 0.60 the UI shows a "?" badge and offers "Assign to app…", so a resolver that
    /// got the id right by a route that does not deserve 0.95 is still wrong.
    /// </summary>
    [Fact]
    [Trait("Category", "S2")]
    public void Resolve_HonoursTheConfidenceTableWhereAFixtureAssertsIt()
    {
        foreach (var fixture in Fixtures)
        {
            foreach (var (process, result) in FixtureEnvironment.ResolveAll(fixture))
            {
                if (process.ExpectConfidence is not { } expected)
                {
                    continue;
                }

                result.Confidence.ShouldBe(
                    expected,
                    tolerance: 0.0001,
                    $"{fixture.Name}: pid {process.Pid} ({process.ImageName}) resolved via {result.DecidedBy}");
            }
        }
    }

    /// <summary>
    /// The zero-touch claim. A fixture that marks a process Tier 2 is asserting that the enrichment adapter
    /// would never have called <c>OpenProcess</c> on it — the safety claim the whole anti-cheat story rests
    /// on, so the gate checks it here rather than trusting that some other suite does
    /// (docs/11_SAFETY_POLICY.md §Process access tiers).
    /// </summary>
    /// <remarks>
    /// Entries flagged <c>tierNeedsPromotion</c> are excluded, because their Tier 2 comes from a signal no
    /// pure function of a path carries: a protected game's own executable has no anti-cheat name and sits in
    /// no anti-cheat directory, and what promotes it is a driver being loaded and a helper that starts after
    /// it. That promotion is a collector mechanism and is not built yet (docs/24_ADR.md §Findings).
    /// </remarks>
    [Fact]
    [Trait("Category", "S2")]
    public void Policy_ClassifiesZeroTouchProcessesFromTheirPathAlone()
    {
        foreach (var fixture in Fixtures)
        {
            foreach (var process in fixture.Processes
                .Where(p => p.ExpectTier is not null && !p.TierNeedsPromotion))
            {
                var tier = FixtureEnvironment.Policy.TierOfProcess(process.Image, process.ImageName);

                ((int)tier).ShouldBe(
                    process.ExpectTier!.Value,
                    $"{fixture.Name}: pid {process.Pid} ({process.ImageName}) at {process.Image}");
            }
        }
    }

    /// <summary>
    /// What the shipping Agent actually does today, which is not what the fixtures measure. Its
    /// <c>IdentityIndexes</c> are empty until the registry and manifest readers land, so every expectation
    /// that depends on an index must degrade — and everything that does not must be unaffected.
    /// </summary>
    /// <remarks>
    /// docs/03 §What ships makes a specific claim about this ("Chrome, Discord, Steam and VS Code get their
    /// `cat:` ids; a Steam game gets `root:` rather than `steam:&lt;appid&gt;`"). A claim in a doc that no
    /// test holds is a claim that goes stale the first time someone changes the pipeline, so it is asserted
    /// here instead of trusted.
    /// </remarks>
    [Fact]
    public void Resolve_WithEmptyIndexes_KeepsCatalogIdentityAndLosesOnlyIndexIdentity()
    {
        var byName = Fixtures.ToDictionary(f => f.Name, StringComparer.Ordinal);

        Expect("01_chrome", 4100, "cat:chrome");
        Expect("02_discord", 5100, "cat:discord");
        Expect("03_steam_game_anticheat", 6100, "cat:steam");
        Expect("08_vscode_git_node", 11100, "cat:vscode");

        // Package identity comes from the process, not from an index, so it survives.
        Expect("04_windows_terminal", 7100, "msix:Microsoft.WindowsTerminal_8wekyb3d8bbwe");

        // And the two that genuinely depend on an index degrade, each to the honest answer rather than to
        // the launcher's id - which is the one thing the gate will not tolerate at any index fidelity.
        var game = Resolve("03_steam_game_anticheat", 6200);
        game.AppId.Value.ShouldStartWith("root:", Case.Sensitive, "a Steam game without the manifest index");
        FixtureEnvironment.LauncherAppIds.ShouldNotContain(game.AppId.Value);

        Resolve("08_vscode_git_node", 11103).AppId.Value
            .ShouldStartWith("root:", Case.Sensitive, "git.exe without the uninstall index");

        void Expect(string fixture, int pid, string id) =>
            Resolve(fixture, pid).AppId.Value.ShouldBe(id, $"{fixture} pid {pid} with empty indexes");

        ResolutionResult Resolve(string fixture, int pid)
        {
            var resolved = FixtureEnvironment.ResolveAllWithEmptyIndexes(byName[fixture]);
            return resolved.Single(r => r.Process.Pid == pid).Result;
        }
    }

    /// <summary>
    /// <c>tierNeedsPromotion</c> is an admission that the code is behind the spec, so it has to be earned:
    /// the fixture claiming it must actually describe an anti-cheat driver or service, or the flag would be
    /// a way to excuse any tier miss at all.
    /// </summary>
    [Fact]
    public void Fixtures_PromotionOnlyClaimedWhereAnAntiCheatSignalExists()
    {
        foreach (var fixture in Fixtures)
        {
            foreach (var process in fixture.Processes.Where(p => p.TierNeedsPromotion))
            {
                process.ExpectTier.ShouldBe(2, $"{fixture.Name}: pid {process.Pid} promotes to nothing");

                (fixture.Indexes.LoadedDrivers.Count + fixture.Indexes.Services.Count)
                    .ShouldBeGreaterThan(
                        0,
                        $"{fixture.Name}: pid {process.Pid} claims a promotion with no driver or service "
                        + "in the fixture to promote it");
            }
        }
    }
}
