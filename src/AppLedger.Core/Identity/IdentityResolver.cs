using AppLedger.Core.Catalog;
using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// The resolver docs/03_APP_IDENTITY.md specifies: ten steps from a process instance to a stable
/// <c>app_id</c>, gated by spike S2.
/// </summary>
/// <remarks>
/// Two things about the shape are worth knowing before reading it.
/// <para>
/// <b>The Tier-0 check is not step 1; it is the fallback.</b> docs/03 numbers it first and says "stop", but
/// two S2 fixtures contradict that and they are the gate. <c>conhost.exe</c> lives in <c>System32</c> and
/// must still join the script its parent is running — fixture 4 says so in as many words, "conhost is
/// attach_parent, it must NOT fall into the system rule first" — and fixtures 4 and 10 both expect
/// <c>msix:</c> for images under <c>WindowsApps</c>, which is Tier 0 so that nobody *scans* it rather than
/// because a packaged app is part of Windows. So host rules run first, identity sources run next, and a
/// Tier-0 path is what makes something <c>sys:windows</c> only when nothing else has claimed it — which is
/// exactly how plain <c>notepad.exe</c> still lands there (docs/24_ADR.md §Findings).
/// </para>
/// <para>
/// <b>It is stateful, and has to be.</b> Adoption and <c>attach_parent</c> need the parent's answer, so
/// resolutions are cached per <c>(pid, createTime)</c> for the instance lifetime — which is also what
/// docs/03 §Caching asks for. Instances therefore have to arrive in ascending create-time order, which the
/// collector produces naturally and the fixtures guarantee. <see cref="Invalidate"/> drops the cache.
/// </para>
/// </remarks>
public sealed class IdentityResolver : IIdentityResolver
{
    private readonly IPolicyGuard _policy;
    private readonly InstallRootHeuristic _installRoots;
    private readonly Func<IdentityContext> _context;
    private readonly IReadOnlyList<CatalogHostRule> _hostRules;
    private readonly IReadOnlyList<CompiledApp> _apps;
    private readonly IReadOnlyList<CatalogAntiCheat> _antiCheat;
    private readonly IReadOnlyDictionary<string, string> _systemNames;
    private readonly Dictionary<ProcessKey, Resolution> _cache = [];

    /// <summary>Creates a resolver over a verified catalog.</summary>
    /// <param name="catalog">The loaded catalog; its globs are expanded once, here.</param>
    /// <param name="expander">Supplies the real values of the <c>%VAR%</c> tokens the catalog may use.</param>
    /// <param name="policy">Decides path tiers, so a Windows component is recognised without a handle.</param>
    /// <param name="installRoots">The install-root heuristic of docs/03 §Install root heuristic.</param>
    /// <param name="context">
    /// Supplies the current indexes and overrides. A delegate rather than a value because both change while
    /// the Agent runs — a new install, a user correction — and the resolver outlives them.
    /// </param>
    public IdentityResolver(
        CatalogDocument catalog,
        EnvExpander expander,
        IPolicyGuard policy,
        InstallRootHeuristic installRoots,
        Func<IdentityContext> context)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(expander);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentNullException.ThrowIfNull(context);

        _policy = policy;
        _installRoots = installRoots;
        _context = context;
        _hostRules = HostRuleMatcher.InEvaluationOrder(catalog.HostRules);
        _antiCheat = catalog.AntiCheat;
        _systemNames = catalog.SystemDisplayNames;

        var launchers = new HashSet<string>(catalog.Launchers, StringComparer.OrdinalIgnoreCase);
        _apps = [.. catalog.Apps.Select(app => CompiledApp.Compile(app, expander, launchers))];
    }

    /// <inheritdoc />
    public ResolutionResult Resolve(ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (_cache.TryGetValue(facts.Key, out var cached))
        {
            return cached.Result;
        }

        var resolution = Classify(facts, _context());
        _cache[facts.Key] = resolution;
        return resolution.Result;
    }

    /// <inheritdoc />
    public void Invalidate() => _cache.Clear();

    private Resolution Classify(ProcessFacts facts, IdentityContext ctx)
    {
        // Step 0. A user override is the user telling us the grouping is wrong, so nothing outranks it.
        if (FindOverride(facts, ctx) is { } overridden)
        {
            return Terminal(
                overridden.Value,
                AppSource.User,
                Confidence.Certain,
                ResolutionStep.UserOverride,
                "user override",
                DisplayNameFor(facts));
        }

        // Steps 1-2. Host rules, in the canonical order of docs/03 - see the remarks on why they come first.
        // A rule that returns null has matched but declined to decide ("no script argument", "parent
        // exited"), and docs/03 says such a process resolves independently rather than falling to the next
        // rule: the rule it matched is still the right rule.
        foreach (var rule in _hostRules)
        {
            if (!HostRuleMatcher.Matches(rule, facts.Key.Pid, facts.ImageFileName, facts.CommandLine))
            {
                continue;
            }

            if (ApplyHostRule(rule, facts, ctx) is { } decided)
            {
                return decided;
            }

            break;
        }

        // Steps 3-7. Everything that gives a process an identity of its own. They are gathered rather than
        // short-circuited because docs/03 lets a later step win on precedence, and the losers are worth
        // keeping: apps.aliases_json is what makes a later merge cheap.
        var installRoot = _installRoots.FromImagePath(facts.ImagePath);
        var candidates = new List<Candidate>();
        CompiledApp? matchedApp = null;

        AddPackageIdentity(facts, candidates);
        AddLauncherManifests(facts, ctx, candidates);
        AddCatalogRules(facts, candidates, ref matchedApp);
        AddUninstall(facts, ctx, candidates);
        AddPackageManagers(facts, ctx, candidates);

        if (candidates.Count > 0)
        {
            return Best(candidates, facts, matchedApp, installRoot);
        }

        // Step 1, as the fallback it actually is. A Tier-0 path makes something a Windows component only
        // once nothing else has claimed it: WindowsApps is Tier 0 so that nobody *scans* it, and an MSIX
        // app living there is a real app rather than part of Windows. S2 fixtures 4 and 10 are explicit
        // about it - both expect msix: for images under WindowsApps - while plain notepad.exe, which no
        // rule and no index claims, lands here and becomes sys:windows (docs/24_ADR.md §Findings).
        if (facts.ImagePath is not null && _policy.TierOf(facts.ImagePath) == PathTier.ProtectedOs)
        {
            return Terminal(
                AppId.Windows,
                AppSource.System,
                Confidence.Certain,
                ResolutionStep.ProtectedOsPath,
                "protected OS path",
                DisplayNameFor(facts));
        }

        // Step 8. Adoption, only now: a child with an identity of its own is always its own app, which is
        // adoption rule 3 and the reason a game is never merged into its launcher.
        if (Adopt(facts, installRoot) is { } adopted)
        {
            return adopted;
        }

        // Steps 9-10. PE metadata raises the confidence of the install-root hash but not its id: the id is
        // still a hash of a directory, and saying otherwise would make two products in one vendor folder
        // look like a sure thing.
        var named = facts.ProductName is { Length: > 0 } && facts.Signer is { Length: > 0 };
        var root = installRoot ?? facts.ImageFileName;

        return new Resolution(
            new ResolutionResult
            {
                AppId = AppId.Root(root),
                Source = AppSource.Root,
                Confidence = named ? Confidence.PeAndSigner : Confidence.RootFallback,
                DecidedBy = named ? ResolutionStep.PeMetadata : ResolutionStep.RootFallback,
                DisplayName = DisplayNameFor(facts),
                InstallRoot = installRoot,
                Evidence =
                [
                    new IdentityEvidence(
                        named ? ResolutionStep.PeMetadata : ResolutionStep.RootFallback,
                        installRoot is null ? "image name only" : "install root"),
                ],
            },
            MatchedApp: null,
            InstallRoot: installRoot);
    }

    // --- Host rules ------------------------------------------------------------------------------

    private Resolution? ApplyHostRule(CatalogHostRule rule, ProcessFacts facts, IdentityContext ctx) =>
        rule.Rule switch
        {
            HostRuleKind.Fixed => FixedRule(rule, facts),
            HostRuleKind.System => System(facts, "system host rule"),
            HostRuleKind.ServiceGroup => ServiceGroup(facts),
            HostRuleKind.DllArgOrSystem => DllArgOrSystem(facts, ctx),
            HostRuleKind.AttachParent => AttachParent(facts),
            HostRuleKind.ScriptFromCmdline => ScriptFromCmdline(facts),
            HostRuleKind.AnticheatHelper => AntiCheatHelper(facts, ctx),
            _ => null,
        };

    private static Resolution? FixedRule(CatalogHostRule rule, ProcessFacts facts) =>
        rule.AppId is { Length: > 0 } id && AppId.TryParse(id, out var parsed)
            ? Terminal(parsed, AppSource.System, Confidence.Certain, ResolutionStep.HostRule, "fixed rule", DisplayNameFor(facts))
            : null;

    private static Resolution System(ProcessFacts facts, string why) =>
        Terminal(AppId.Windows, AppSource.System, Confidence.Certain, ResolutionStep.HostRule, why, DisplayNameFor(facts));

    /// <summary>
    /// <c>svchost.exe -k &lt;group&gt; -p -s &lt;Service&gt;</c>. With <c>-s</c> the host owns exactly one
    /// service and is named for it; without it, several share the process and attributing bytes to one of
    /// them would be a guess, so they share a bucket (docs/03 §Host rules).
    /// </summary>
    private Resolution ServiceGroup(ProcessFacts facts)
    {
        var service = ValueAfter(facts.CommandLine, "-s");

        if (service is null)
        {
            return Terminal(
                AppId.ServiceGroup, AppSource.System, Confidence.Certain, ResolutionStep.HostRule,
                "service host without -s", "Service host");
        }

        return Terminal(
            AppId.Service(service), AppSource.System, Confidence.Certain, ResolutionStep.HostRule,
            "service host", DisplayNameOfService(service));
    }

    /// <summary>
    /// <c>rundll32</c> and friends: the DLL named on the command line is what is actually running, so if an
    /// app claims it the process belongs to that app. Otherwise it is a Windows component.
    /// </summary>
    private Resolution DllArgOrSystem(ProcessFacts facts, IdentityContext ctx)
    {
        foreach (var token in Tokenize(facts.CommandLine).Skip(1))
        {
            if (!token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var app in _apps)
            {
                if (app.CoversPath(token))
                {
                    return Terminal(
                        AppId.Catalog(app.App.Id), AppSource.Catalog, Confidence.CatalogRule,
                        ResolutionStep.HostRule, "dll under " + app.App.Id, app.App.Name);
                }
            }

            if (ctx.Indexes.ClaimOfUninstall(token) is { } claim)
            {
                return Terminal(
                    AppId.Uninstall(claim.Entry.Key), AppSource.Uninstall, claim.Confidence,
                    ResolutionStep.HostRule, "dll under an installed app",
                    claim.Entry.DisplayName ?? facts.ImageFileName);
            }
        }

        return System(facts, "dll host with no claimed dll");
    }

    /// <summary>
    /// Chromium children, crash handlers and <c>conhost</c> belong to whoever started them. A parent that is
    /// unknown or already gone leaves nothing to attach to, and docs/03 says such a process resolves
    /// independently rather than being dropped.
    /// </summary>
    private Resolution? AttachParent(ProcessFacts facts)
    {
        if (ParentOf(facts) is not { } parent)
        {
            return null;
        }

        return new Resolution(
            parent.Result with
            {
                Confidence = Confidence.Adopted(parent.Result.Confidence),
                DecidedBy = ResolutionStep.HostRule,
                DisplayName = parent.Result.DisplayName,

                // Cleared, as in Adopt: the parent's aliases describe how the *parent* was identified, and
                // the app row they belong to was already written from it. Carrying them down every child
                // would rewrite the same list once per instance and say nothing new about this one.
                Aliases = [],
                Evidence = [new IdentityEvidence(ResolutionStep.HostRule, "attached to parent")],
            },
            parent.MatchedApp,
            parent.InstallRoot);
    }

    /// <summary>
    /// A runtime is not the app; the thing it was told to run is. <c>-m</c> names a module rather than a
    /// file, and a runtime started with no script at all falls through to its own install root.
    /// </summary>
    private static Resolution? ScriptFromCmdline(ProcessFacts facts)
    {
        if (ScriptTarget(facts.CommandLine) is not { } target)
        {
            return null;
        }

        var leaf = PathRules.LeafName(target) ?? target;

        return Terminal(
            AppId.Script(target), AppSource.Script, Confidence.Script, ResolutionStep.HostRule,
            "script from command line", DisplayNameOfScript(facts, leaf));
    }

    /// <summary>
    /// An anti-cheat helper inside a game's directory is part of that game; one installed on its own is its
    /// own app. Either way it is Tier 2 and we never open a handle to it (docs/11_SAFETY_POLICY.md).
    /// </summary>
    private Resolution? AntiCheatHelper(ProcessFacts facts, IdentityContext ctx)
    {
        if (GameClaim(facts, ctx) is { } game)
        {
            return Terminal(
                game.Id, game.Source, Confidence.Certain, ResolutionStep.HostRule,
                "anti-cheat helper inside " + game.Id.Value, game.Name ?? DisplayNameFor(facts));
        }

        if (FindAntiCheat(facts) is { } entry)
        {
            return Terminal(
                AppId.Catalog(entry.Id), AppSource.Catalog, Confidence.CatalogRule, ResolutionStep.HostRule,
                "anti-cheat " + entry.Id, entry.Name);
        }

        return null;
    }

    private CatalogAntiCheat? FindAntiCheat(ProcessFacts facts)
    {
        var components = (facts.ImagePath ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in _antiCheat)
        {
            foreach (var dir in entry.Dirs)
            {
                if (components.Any(c => string.Equals(c, dir, StringComparison.OrdinalIgnoreCase))
                    || facts.ImageFileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    // --- Own-identity steps ----------------------------------------------------------------------

    private static void AddPackageIdentity(ProcessFacts facts, List<Candidate> candidates)
    {
        if (facts.PackageFamilyName is { Length: > 0 } family)
        {
            candidates.Add(new Candidate(
                AppId.Msix(family), AppSource.Msix, Confidence.Certain, ResolutionStep.PackageIdentity,
                "package family name", null));
        }
    }

    private static void AddLauncherManifests(ProcessFacts facts, IdentityContext ctx, List<Candidate> candidates)
    {
        Consider(ctx.Indexes.Steam, AppSource.Steam, AppId.Steam);
        Consider(ctx.Indexes.Epic, AppSource.Epic, AppId.Epic);
        Consider(ctx.Indexes.Gog, AppSource.Gog, AppId.Gog);
        Consider(ctx.Indexes.Itch, AppSource.Itch, AppId.Itch);

        void Consider(
            IReadOnlyList<LauncherIndexEntry> entries, AppSource source, Func<string, AppId> mint)
        {
            if (IdentityIndexes.ClaimOf(entries, facts.ImagePath) is { } entry)
            {
                candidates.Add(new Candidate(
                    mint(entry.Id), source, Confidence.Certain, ResolutionStep.LauncherManifest,
                    "launcher manifest", entry.Name));
            }
        }
    }

    private void AddCatalogRules(ProcessFacts facts, List<Candidate> candidates, ref CompiledApp? matched)
    {
        foreach (var app in _apps)
        {
            var kind = app.Match(facts);
            if (kind == CatalogMatchKind.None)
            {
                continue;
            }

            matched ??= app;

            // A rule whose only criterion is the package family tells us nothing msix: does not already
            // say, and S2 fixture 4 expects msix:...Terminal rather than cat:windows-terminal. Such an
            // entry still supplies the category and the helper list - which is how OpenConsole.exe and the
            // Xbox app's children are grouped - so it is kept as the matched app and not as an identity
            // (docs/24_ADR.md §Findings).
            if (app.PackageFamilyOnly)
            {
                continue;
            }

            candidates.Add(new Candidate(
                AppId.Catalog(app.App.Id), AppSource.Catalog, Confidence.CatalogRule, ResolutionStep.CatalogRule,
                kind == CatalogMatchKind.Helper ? "catalog helper of " + app.App.Id : "catalog rule " + app.App.Id,
                app.App.Name,
                app));
        }
    }

    private static void AddUninstall(ProcessFacts facts, IdentityContext ctx, List<Candidate> candidates)
    {
        if (ctx.Indexes.ClaimOfUninstall(facts.ImagePath) is { } claim)
        {
            candidates.Add(new Candidate(
                AppId.Uninstall(claim.Entry.Key), AppSource.Uninstall, claim.Confidence,
                ResolutionStep.UninstallIndex, "uninstall entry " + claim.Entry.Key, claim.Entry.DisplayName));
        }
    }

    private static void AddPackageManagers(ProcessFacts facts, IdentityContext ctx, List<Candidate> candidates)
    {
        Consider(ctx.Indexes.Scoop, AppSource.Scoop, AppId.Scoop);
        Consider(ctx.Indexes.Choco, AppSource.Choco, AppId.Choco);

        void Consider(IReadOnlyList<LauncherIndexEntry> entries, AppSource source, Func<string, AppId> mint)
        {
            if (IdentityIndexes.ClaimOf(entries, facts.ImagePath) is { } entry)
            {
                candidates.Add(new Candidate(
                    mint(entry.Id), source, Confidence.PackageManager, ResolutionStep.PackageManager,
                    "package manager directory", entry.Name));
            }
        }
    }

    private static Resolution Best(
        List<Candidate> candidates, ProcessFacts facts, CompiledApp? matchedApp, string? installRoot)
    {
        var winner = candidates[0];
        foreach (var candidate in candidates)
        {
            if (candidate.Source.Wins(winner.Source))
            {
                winner = candidate;
            }
        }

        var aliases = candidates
            .Where(c => c.Id != winner.Id)
            .Select(c => c.Id)
            .Distinct()
            .ToArray();

        return new Resolution(
            new ResolutionResult
            {
                AppId = winner.Id,
                Source = winner.Source,
                Confidence = winner.Confidence,
                DecidedBy = winner.Step,
                DisplayName = winner.Name ?? DisplayNameFor(facts),
                InstallRoot = installRoot,
                Aliases = aliases,
                Evidence = [.. candidates.Select(c => new IdentityEvidence(c.Step, c.Why))],
            },
            // The app that produced the *winning* id, not whichever matched first. They differ only when
            // two catalog entries claim one process, which AND-across-kinds makes rare - but "is my parent
            // a Launcher?" is answered from this, and answering it from a losing match would be wrong in
            // exactly the case the S2 gate exists to catch.
            winner.App ?? matchedApp,
            installRoot);
    }

    // --- Adoption --------------------------------------------------------------------------------

    /// <summary>
    /// Adoption rule 2 of docs/03 §Parent adoption, with rule 3 already satisfied by the caller: a child is
    /// only offered to its parent once it has been found to have no identity of its own. A launcher's own
    /// helpers come here; its games never do, because a game has a manifest identity.
    /// </summary>
    private Resolution? Adopt(ProcessFacts facts, string? installRoot)
    {
        if (ParentOf(facts) is not { } parent)
        {
            return null;
        }

        // The test is that the child's *own* install root is the parent's, not that its image sits
        // somewhere under it. The two read alike and differ on exactly the case this gate exists for: a
        // Steam game lives inside the Steam folder, so "under the parent's install root" is true of a game
        // as well as of a helper, and adoption would merge the game into its launcher. The install-root
        // heuristic already treats `steamapps\common` as a boundary, so the game's own root is its own
        // directory while `steamwebhelper.exe`, four levels down in `bin\cef\...`, still shares Steam's.
        //
        // This is what docs/03 adoption rule 2's launcher clause is protecting - "children under a game
        // root resolve to the game, a game is never merged into its launcher" - and it has to hold whether
        // or not the launcher manifest index happens to be populated, which is why it is a root comparison
        // here rather than a consequence of the game having a manifest identity.
        if (parent.InstallRoot is null || !PathRules.SamePath(installRoot, parent.InstallRoot))
        {
            return null;
        }

        return new Resolution(
            parent.Result with
            {
                Confidence = Confidence.Adopted(parent.Result.Confidence),
                DecidedBy = ResolutionStep.ParentAdoption,
                Aliases = [],
                Evidence = [new IdentityEvidence(ResolutionStep.ParentAdoption, "adopted by parent")],
            },
            parent.MatchedApp,
            parent.InstallRoot);
    }

    /// <summary>
    /// The parent's resolution, or null. Looked up by the whole <c>(pid, createTime)</c> key and checked for
    /// ordering, which is the PID-reuse guard: a recycled pid must not let an unrelated process inherit the
    /// identity of whatever used to own that number (S2 fixture 11).
    /// </summary>
    private Resolution? ParentOf(ProcessFacts facts)
    {
        if (facts.Parent is not { } key || !_cache.TryGetValue(key, out var parent))
        {
            return null;
        }

        return key.CreateTime < facts.Key.CreateTime ? parent : null;
    }

    private static LauncherGame? GameClaim(ProcessFacts facts, IdentityContext ctx)
    {
        if (IdentityIndexes.ClaimOf(ctx.Indexes.Steam, facts.ImagePath) is { } steam)
        {
            return new LauncherGame(AppId.Steam(steam.Id), AppSource.Steam, steam.Name);
        }

        if (IdentityIndexes.ClaimOf(ctx.Indexes.Epic, facts.ImagePath) is { } epic)
        {
            return new LauncherGame(AppId.Epic(epic.Id), AppSource.Epic, epic.Name);
        }

        if (IdentityIndexes.ClaimOf(ctx.Indexes.Gog, facts.ImagePath) is { } gog)
        {
            return new LauncherGame(AppId.Gog(gog.Id), AppSource.Gog, gog.Name);
        }

        return IdentityIndexes.ClaimOf(ctx.Indexes.Itch, facts.ImagePath) is { } itch
            ? new LauncherGame(AppId.Itch(itch.Id), AppSource.Itch, itch.Name)
            : null;
    }

    // --- Overrides -------------------------------------------------------------------------------

    /// <summary>
    /// The first override that claims this process. <see cref="OverrideKind"/> is deliberately not consulted:
    /// a merge and a split both answer "this instance belongs to that app", and they differ only in what the
    /// UI did to get there and in the <c>previous_app_id</c> it keeps for the undo window
    /// (docs/03_APP_IDENTITY.md §User overrides).
    /// </summary>
    private static IdentityOverride? FindOverride(ProcessFacts facts, IdentityContext ctx)
    {
        foreach (var rule in ctx.Overrides)
        {
            if (PathRules.SamePath(rule.Match, facts.ImagePath)
                || (facts.CommandLine is { Length: > 0 } cmd
                    && cmd.Contains(rule.Match, StringComparison.OrdinalIgnoreCase)))
            {
                return rule;
            }
        }

        return null;
    }

    // --- Command-line reading --------------------------------------------------------------------

    /// <summary>
    /// The script or module a runtime was told to run, or null when it was told to run nothing. <c>-m</c> is
    /// read first because a module name need not look like a file: <c>-m mymodule</c> has no extension, and
    /// without this it would fall through to the runtime's own install root.
    /// </summary>
    internal static string? ScriptTarget(string? commandLine)
    {
        var tokens = Tokenize(commandLine).Skip(1).ToArray();

        for (var i = 0; i < tokens.Length; i++)
        {
            if (IsOption(tokens[i], "m") && i + 1 < tokens.Length)
            {
                return tokens[i + 1];
            }
        }

        foreach (var token in tokens)
        {
            if (token.Length == 0 || token[0] is '-' or '/')
            {
                continue;
            }

            // A path needs an extension to be a script rather than an argument. `?:\x` style absolute paths
            // carry a colon at index 1, which is not an extension, hence LeafName before the dot test.
            var leaf = PathRules.LeafName(token) ?? token;
            if (leaf.Contains('.', StringComparison.Ordinal) && !leaf.EndsWith('.'))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>The value after a switch, e.g. <c>Schedule</c> from <c>-k netsvcs -p -s Schedule</c>.</summary>
    internal static string? ValueAfter(string? commandLine, string option)
    {
        var name = option.TrimStart('-', '/');
        var tokens = Tokenize(commandLine).ToArray();

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (IsOption(tokens[i], name))
            {
                return tokens[i + 1];
            }
        }

        return null;
    }

    private static bool IsOption(string token, string name) =>
        token.Length == name.Length + 1
        && token[0] is '-' or '/'
        && token.AsSpan(1).Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a command line on whitespace, honouring double quotes. Not a full <c>CommandLineToArgvW</c> -
    /// it does not need to be, because every consumer here only asks "is this token an option" and "does it
    /// look like a path", and a backslash before a quote never changes either answer.
    /// </summary>
    internal static IEnumerable<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            yield break;
        }

        var start = -1;
        var quoted = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];

            if (ch == '"')
            {
                quoted = !quoted;
                if (start < 0)
                {
                    start = i + 1;
                }

                continue;
            }

            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (start >= 0)
                {
                    yield return Trim(commandLine, start, i);
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0 && start < commandLine.Length)
        {
            yield return Trim(commandLine, start, commandLine.Length);
        }

        static string Trim(string value, int from, int to) => value[from..to].Trim('"');
    }

    // --- Display names ---------------------------------------------------------------------------

    private string DisplayNameOfService(string service) =>
        _systemNames.TryGetValue(service, out var friendly) ? friendly : service;

    /// <summary>"Python — scraper.py", as docs/03 §Host rules spells it.</summary>
    private static string DisplayNameOfScript(ProcessFacts facts, string leaf)
    {
        var runtime = FirstNonEmpty(facts.ProductName, facts.FileDescription)
            ?? Path.GetFileNameWithoutExtension(facts.ImageFileName);

        return string.IsNullOrEmpty(runtime) ? leaf : runtime + " — " + leaf;
    }

    private static string DisplayNameFor(ProcessFacts facts) =>
        FirstNonEmpty(facts.FileDescription, facts.ProductName, facts.ImageFileName) ?? "(unknown)";

    private static string? FirstNonEmpty(params string?[] candidates) =>
        Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static Resolution Terminal(
        AppId id, AppSource source, double confidence, ResolutionStep step, string why, string? displayName) =>
        new(
            new ResolutionResult
            {
                AppId = id,
                Source = source,
                Confidence = confidence,
                DecidedBy = step,
                DisplayName = displayName,
                Evidence = [new IdentityEvidence(step, why)],
            },
            MatchedApp: null,
            InstallRoot: null);

    // --- Shapes ----------------------------------------------------------------------------------

    /// <summary>A resolution plus the two facts a child needs about its parent.</summary>
    private sealed record Resolution(ResolutionResult Result, CompiledApp? MatchedApp, string? InstallRoot);

    /// <summary>One identity a step proposed, before precedence picks between them.</summary>
    private readonly record struct Candidate(
        AppId Id,
        AppSource Source,
        double Confidence,
        ResolutionStep Step,
        string Why,
        string? Name,
        CompiledApp? App = null);

    /// <summary>A game a launcher manifest claims, used by the anti-cheat rule.</summary>
    private readonly record struct LauncherGame(AppId Id, AppSource Source, string? Name);

    private enum CatalogMatchKind
    {
        None,
        Rule,
        Helper,
    }

    /// <summary>A catalog app with its globs expanded once, at construction.</summary>
    private sealed record CompiledApp(
        CatalogApp App, IReadOnlyList<PathGlob> Roots, bool PackageFamilyOnly, bool IsLauncher)
    {
        public static CompiledApp Compile(CatalogApp app, EnvExpander expander, HashSet<string> launchers)
        {
            var roots = new List<PathGlob>();
            foreach (var pattern in app.Match.InstallRootGlob)
            {
                if (PathGlob.TryParse(expander.Expand(pattern), out var glob) && glob is not null)
                {
                    roots.Add(glob);
                }
            }

            var packageOnly = app.Match.PackageFamily.Count > 0
                && app.Match.Signer.Count == 0
                && app.Match.Exe.Count == 0
                && app.Match.InstallRootGlob.Count == 0;

            return new CompiledApp(app, roots, packageOnly, launchers.Contains(app.Id));
        }

        public bool CoversPath(string? path) => Roots.Any(r => r.MatchesOrContains(path));

        /// <summary>
        /// Fields of different kinds are AND-ed and values within one kind are OR-ed
        /// (docs/13_CATALOG_RULES.md §Matching semantics), which is what keeps a portable 7-Zip from
        /// stealing <c>cat:7zip</c> on the strength of its signature alone.
        /// </summary>
        public CatalogMatchKind Match(ProcessFacts facts)
        {
            if (Matches(facts))
            {
                return CatalogMatchKind.Rule;
            }

            return MatchesHelper(facts) ? CatalogMatchKind.Helper : CatalogMatchKind.None;
        }

        private bool Matches(ProcessFacts facts)
        {
            if (App.Match.Signer.Count > 0 && !Any(App.Match.Signer, facts.Signer))
            {
                return false;
            }

            if (App.Match.Exe.Count > 0 && !NamesExecutable(facts))
            {
                return false;
            }

            if (App.Match.PackageFamily.Count > 0 && !Any(App.Match.PackageFamily, facts.PackageFamilyName))
            {
                return false;
            }

            return App.Match.InstallRootGlob.Count <= 0 || CoversPath(facts.ImagePath);
        }

        /// <summary>
        /// A helper is only a helper inside the app it helps. Without the root test, every process with
        /// <c>--type=</c> anywhere on the machine would join whichever Chromium app the catalog listed first.
        /// </summary>
        private bool MatchesHelper(ProcessFacts facts)
        {
            if (App.Helpers is not { } helpers || Roots.Count == 0 || !CoversPath(facts.ImagePath))
            {
                return false;
            }

            if (NamesAny(helpers.Exe, facts))
            {
                return true;
            }

            return facts.CommandLine is { Length: > 0 } cmd
                && helpers.CmdlineContains.Any(n => cmd.Contains(n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Tested against both the ETW image name and the leaf of the queried full path, because they can
        /// disagree: S2 fixture 8 has an extension host whose ETW name is <c>node.exe</c> while its image is
        /// <c>Code.exe</c>, and the full path is the more authoritative of the two.
        /// </summary>
        private bool NamesExecutable(ProcessFacts facts) => NamesAny(App.Match.Exe, facts);

        private static bool NamesAny(IReadOnlyList<string> names, ProcessFacts facts)
        {
            var leaf = facts.ImagePath is null ? null : PathRules.LeafName(facts.ImagePath);
            return Any(names, facts.ImageFileName) || Any(names, leaf);
        }

        private static bool Any(IReadOnlyList<string> values, string? candidate) =>
            candidate is { Length: > 0 }
            && values.Any(v => string.Equals(v, candidate, StringComparison.OrdinalIgnoreCase));
    }
}
