namespace AppLedger.Core.Catalog;

/// <summary>
/// Decides whether a host rule applies to a process. Fields are OR-ed with each other and within each
/// list (docs/13_CATALOG_RULES.md §Matching semantics) — a rule that needs two conditions to hold at once
/// is expressed as its own rule kind, not as two fields.
/// </summary>
public static class HostRuleMatcher
{
    /// <summary>
    /// True when the rule selects this process. <paramref name="commandLine"/> may be null for a Tier-2
    /// process, where we never read one; matching then falls back to the name-based signals only.
    /// </summary>
    public static bool Matches(CatalogHostRule rule, int pid, string? imageFileName, string? commandLine)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Pid is { } wanted && pid == wanted)
        {
            return true;
        }

        if (imageFileName is { Length: > 0 })
        {
            foreach (var exe in rule.Exe)
            {
                if (string.Equals(exe, imageFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var pattern in rule.ExeGlob)
            {
                if (PathGlob.WildcardMatch(pattern, imageFileName))
                {
                    return true;
                }
            }
        }

        if (commandLine is { Length: > 0 })
        {
            foreach (var needle in rule.CmdlineContains)
            {
                if (commandLine.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The first rule in catalog order that selects the process, or null. Order matters: docs/03 fixes it
    /// as fixed, system, service_group, dll_arg_or_system, attach_parent, script_from_cmdline,
    /// anticheat_helper, launcher_children, and the catalog is expected to list them that way.
    /// </summary>
    public static CatalogHostRule? FirstMatch(
        IReadOnlyList<CatalogHostRule> rules, int pid, string? imageFileName, string? commandLine)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            if (Matches(rule, pid, imageFileName, commandLine))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical evaluation order of docs/03 §Host rules. A catalog that lists rules out of order is
    /// still evaluated in this order, so a rules file cannot change identity semantics by reordering.
    /// </summary>
    public static IReadOnlyList<HostRuleKind> EvaluationOrder { get; } =
    [
        HostRuleKind.Fixed,
        HostRuleKind.System,
        HostRuleKind.ServiceGroup,
        HostRuleKind.DllArgOrSystem,
        HostRuleKind.AttachParent,
        HostRuleKind.ScriptFromCmdline,
        HostRuleKind.AnticheatHelper,
        HostRuleKind.LauncherChildren,
    ];

    /// <summary>Re-orders a catalog's rules into <see cref="EvaluationOrder"/>, keeping ties stable.</summary>
    public static IReadOnlyList<CatalogHostRule> InEvaluationOrder(IReadOnlyList<CatalogHostRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return [.. rules.OrderBy(RankOf)];
    }

    /// <summary>The position of a rule kind in <see cref="EvaluationOrder"/>.</summary>
    private static int RankOf(CatalogHostRule rule)
    {
        for (var i = 0; i < EvaluationOrder.Count; i++)
        {
            if (EvaluationOrder[i] == rule.Rule)
            {
                return i;
            }
        }

        return EvaluationOrder.Count;
    }
}
