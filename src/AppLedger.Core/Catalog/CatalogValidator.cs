namespace AppLedger.Core.Catalog;

/// <summary>
/// The semantic half of strict parsing: everything the JSON schema alone cannot express
/// (docs/13_CATALOG_RULES.md §Strict parsing, §Matching semantics, §Glob grammar).
/// </summary>
public static class CatalogValidator
{
    /// <summary>
    /// The taxonomy every catalog must contain. A file may add categories, never remove one, because an
    /// app row already stored with a removed category would become unreadable.
    /// </summary>
    public static IReadOnlyList<string> BuiltInCategories { get; } =
    [
        "Game", "Browser", "Communication", "DevTool", "Media", "Productivity",
        "Launcher", "Runtime", "Security", "System", "Utility", "Unknown",
    ];

    /// <summary>Throws <see cref="CatalogException"/> on the first rule that fails.</summary>
    public static void Validate(CatalogDocument document, EnvExpander expander)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(expander);

        if (document.Schema != CatalogParser.SupportedSchema)
        {
            throw new CatalogException(
                $"Catalog schema {document.Schema} is not supported (this build understands {CatalogParser.SupportedSchema}).");
        }

        _ = CatalogParser.ParseCalVer(document.Version);

        ValidateCategories(document);
        ValidateApps(document, expander);
        ValidateHostRules(document);
        ValidateAntiCheat(document);
        ValidateCatIdNamespace(document);
        ValidateSensitivePaths(document, expander);
        ValidateProtectedPaths(document, expander);
        ValidateLaunchers(document);
    }

    private static void ValidateCategories(CatalogDocument d)
    {
        var declared = new HashSet<string>(d.Categories, StringComparer.Ordinal);
        foreach (var builtIn in BuiltInCategories)
        {
            if (!declared.Contains(builtIn))
            {
                throw new CatalogException($"Catalog categories must include the built-in category '{builtIn}'.");
            }
        }
    }

    private static void ValidateApps(CatalogDocument d, EnvExpander expander)
    {
        var categories = new HashSet<string>(d.Categories, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var app in d.Apps)
        {
            RequireKebabCase(app.Id, "apps[].id");

            if (!ids.Add(app.Id))
            {
                throw new CatalogException($"Duplicate app id '{app.Id}'.");
            }

            if (!categories.Contains(app.Category))
            {
                throw new CatalogException($"App '{app.Id}' has category '{app.Category}', which is not in `categories`.");
            }

            ValidateMatch(app, expander);

            foreach (var glob in app.DataDirs)
            {
                RequireGlob(glob, expander, $"apps['{app.Id}'].data_dirs");
            }

            foreach (var glob in app.CacheDirs)
            {
                RequireGlob(glob, expander, $"apps['{app.Id}'].cache_dirs");
            }
        }
    }

    /// <summary>
    /// A match is <b>AND across the kinds present</b>, OR within each list. To keep a lone `exe` from
    /// claiming any file with that name — the case that would break S2 fixture 7, portable 7-Zip — an
    /// entry must carry a strong signal or pair `exe` with `install_root_glob`.
    /// </summary>
    private static void ValidateMatch(CatalogApp app, EnvExpander expander)
    {
        var m = app.Match;
        var hasStrong = m.PackageFamily.Count > 0 || m.Signer.Count > 0;
        var hasExe = m.Exe.Count > 0;
        var hasRoot = m.InstallRootGlob.Count > 0;

        if (!hasStrong && !(hasExe && hasRoot))
        {
            throw new CatalogException(
                $"App '{app.Id}' needs `package_family` or `signer`, or both `exe` and `install_root_glob`. "
                + "An exe-only match would claim any file with that name (docs/13 §Matching semantics).");
        }

        foreach (var glob in m.InstallRootGlob)
        {
            RequireGlob(glob, expander, $"apps['{app.Id}'].match.install_root_glob");
        }

        foreach (var exe in m.Exe)
        {
            if (exe.Contains('\\', StringComparison.Ordinal) || exe.Contains('/', StringComparison.Ordinal))
            {
                throw new CatalogException($"App '{app.Id}' lists '{exe}' in `exe`, which must be a bare file name.");
            }
        }
    }

    private static void ValidateHostRules(CatalogDocument d)
    {
        foreach (var rule in d.HostRules)
        {
            var hasSelector = rule.Exe.Count > 0 || rule.ExeGlob.Count > 0 || rule.CmdlineContains.Count > 0 || rule.Pid.HasValue;

            if (rule.Rule == HostRuleKind.Fixed)
            {
                if (string.IsNullOrWhiteSpace(rule.AppId))
                {
                    throw new CatalogException("A `fixed` host rule must carry an `app_id`.");
                }

                if (!rule.AppId.StartsWith("sys:", StringComparison.Ordinal))
                {
                    throw new CatalogException($"A `fixed` host rule may only assign a `sys:*` id, not '{rule.AppId}'.");
                }

                if (!hasSelector)
                {
                    throw new CatalogException($"The `fixed` rule assigning '{rule.AppId}' has no `exe` or `pid`.");
                }

                continue;
            }

            if (rule.AppId is not null)
            {
                throw new CatalogException($"Only `fixed` rules may carry an `app_id`; '{rule.Rule}' does not.");
            }

            // launcher_children selects by the parent's category, so it legitimately has no selector.
            if (!hasSelector && rule.Rule != HostRuleKind.LauncherChildren)
            {
                throw new CatalogException($"Host rule '{rule.Rule}' has no `exe`, `exe_glob` or `cmdline_contains`.");
            }
        }
    }

    private static void ValidateAntiCheat(CatalogDocument d)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in d.AntiCheat)
        {
            RequireKebabCase(entry.Id, "anticheat[].id");

            if (!ids.Add(entry.Id))
            {
                throw new CatalogException($"Duplicate anticheat id '{entry.Id}'.");
            }

            var hasSignal = entry.Services.Count > 0 || entry.Drivers.Count > 0 || entry.Dirs.Count > 0;
            if (entry.MatchConfidence != AntiCheatMatchConfidence.None && !hasSignal)
            {
                throw new CatalogException(
                    $"Anticheat '{entry.Id}' claims match_confidence '{entry.MatchConfidence}' but lists no service, driver or directory.");
            }

            if (entry.MatchConfidence == AntiCheatMatchConfidence.Driver && entry.Drivers.Count == 0)
            {
                throw new CatalogException($"Anticheat '{entry.Id}' claims driver-level matching but lists no driver.");
            }

            if (entry.MatchConfidence == AntiCheatMatchConfidence.Service && entry.Services.Count == 0)
            {
                throw new CatalogException($"Anticheat '{entry.Id}' claims service-level matching but lists no service.");
            }
        }
    }

    /// <summary>
    /// `apps[].id` and `anticheat[].id` both become `cat:&lt;id&gt;` app ids: an app rule mints one when it
    /// matches, and the `anticheat_helper` host rule mints one for a helper outside a game root
    /// (docs/03_APP_IDENTITY.md §Host rules). They therefore share one namespace and must not collide.
    /// </summary>
    private static void ValidateCatIdNamespace(CatalogDocument d)
    {
        var appIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in d.Apps)
        {
            appIds.Add(app.Id);
        }

        foreach (var entry in d.AntiCheat)
        {
            if (appIds.Contains(entry.Id))
            {
                throw new CatalogException(
                    $"'{entry.Id}' is used by both an app rule and an anticheat entry; both mint `cat:{entry.Id}`.");
            }
        }
    }

    private static void ValidateSensitivePaths(CatalogDocument d, EnvExpander expander)
    {
        foreach (var entry in d.SensitivePaths)
        {
            RequireGlob(entry.Glob, expander, "sensitive_paths[].glob");

            if (string.IsNullOrWhiteSpace(entry.Kind))
            {
                throw new CatalogException($"Sensitive path '{entry.Glob}' has no `kind`.");
            }

            RequireKebabCase(entry.Kind, "sensitive_paths[].kind");
        }
    }

    private static void ValidateProtectedPaths(CatalogDocument d, EnvExpander expander)
    {
        foreach (var glob in d.ProtectedPaths)
        {
            RequireGlob(glob, expander, "protected_paths[]");
        }
    }

    private static void ValidateLaunchers(CatalogDocument d)
    {
        var appIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in d.Apps)
        {
            appIds.Add(app.Id);
        }

        foreach (var launcher in d.Launchers)
        {
            if (!appIds.Contains(launcher))
            {
                throw new CatalogException($"`launchers` names '{launcher}', which is not an app id in this catalog.");
            }
        }
    }

    private static void RequireGlob(string glob, EnvExpander expander, string where)
    {
        try
        {
            _ = expander.ExpandToGlob(glob);
        }
        catch (FormatException ex)
        {
            throw new CatalogException($"{where}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ids are lower-case kebab and stable forever: they become `cat:&lt;id&gt;` in every stored row, so a
    /// rename would orphan an app's history.
    /// </summary>
    private static void RequireKebabCase(string value, string where)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new CatalogException($"{where} is empty.");
        }

        if (value[0] == '-' || value[^1] == '-')
        {
            throw new CatalogException($"{where} '{value}' must not start or end with a hyphen.");
        }

        foreach (var ch in value)
        {
            var ok = char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-';
            if (!ok)
            {
                throw new CatalogException($"{where} '{value}' must be lower-case kebab-case.");
            }
        }
    }
}
