namespace AppLedger.Core.Net;

/// <summary>
/// Mozilla's Public Suffix List, used to reduce a host name to its registrable domain (eTLD+1).
/// </summary>
/// <remarks>
/// This is a privacy mechanism, not a convenience: the default host-logging level for non-browser apps
/// stores the registrable domain only, so <c>cdn.discordapp.com</c> is recorded as <c>discordapp.com</c>
/// (docs/10_NETWORK_AND_DNS.md §Host policy, docs/12 §Defaults). Getting the reduction wrong would store
/// more than the user agreed to, which is why the list ships with the app rather than being guessed at.
/// </remarks>
public sealed class PublicSuffixList
{
    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcards;
    private readonly HashSet<string> _exceptions;

    private PublicSuffixList(HashSet<string> rules, HashSet<string> wildcards, HashSet<string> exceptions)
    {
        _rules = rules;
        _wildcards = wildcards;
        _exceptions = exceptions;
    }

    /// <summary>Number of ordinary rules, for diagnostics and for asserting the file actually loaded.</summary>
    public int RuleCount => _rules.Count;

    /// <summary>
    /// Parses the <c>public_suffix_list.dat</c> format: one rule per line, <c>//</c> comments, <c>*</c>
    /// wildcard rules and <c>!</c> exception rules.
    /// </summary>
    public static PublicSuffixList Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rules = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // The list is IDNA-encoded in its ASCII form; we lower-case and compare ordinally.
            line = line.ToLowerInvariant();

            if (line[0] == '!')
            {
                exceptions.Add(line[1..]);
            }
            else if (line.StartsWith("*.", StringComparison.Ordinal))
            {
                wildcards.Add(line[2..]);
            }
            else
            {
                rules.Add(line);
            }
        }

        if (rules.Count == 0)
        {
            throw new FormatException("The public suffix list contained no rules.");
        }

        return new PublicSuffixList(rules, wildcards, exceptions);
    }

    /// <summary>
    /// The public suffix (eTLD) of a host, e.g. <c>co.uk</c> for <c>bbc.co.uk</c>. Returns null for an
    /// IP literal or an empty host.
    /// </summary>
    public string? GetPublicSuffix(string? host)
    {
        var normalized = Normalize(host);
        if (normalized is null)
        {
            return null;
        }

        var labels = normalized.Split('.');

        // Step 2 of the PSL algorithm: an exception rule wins outright and gives back one label.
        for (var i = 0; i < labels.Length; i++)
        {
            if (_exceptions.Contains(Join(labels, i)))
            {
                return Join(labels, i + 1);
            }
        }

        // Step 3: the matching rule with the most labels wins. A wildcard rule `*.foo` matched at `foo`
        // covers one label more than the plain rule `foo` would.
        var bestLabels = 0;
        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var candidate = Join(labels, i);
            var count = labels.Length - i;

            if (_rules.Contains(candidate) && count > bestLabels)
            {
                bestLabels = count;
            }

            if (i > 0 && _wildcards.Contains(candidate) && count + 1 > bestLabels)
            {
                bestLabels = count + 1;
            }
        }

        // Step 4: an unlisted TLD still counts as a public suffix (the implicit `*` rule).
        bestLabels = Math.Clamp(bestLabels == 0 ? 1 : bestLabels, 1, labels.Length);
        return Join(labels, labels.Length - bestLabels);
    }

    private static string Join(string[] labels, int fromIndex) =>
        fromIndex >= labels.Length ? string.Empty : string.Join('.', labels[fromIndex..]);

    /// <summary>
    /// The registrable domain (eTLD+1), e.g. <c>discordapp.com</c> for <c>cdn.discordapp.com</c>.
    /// Returns null for an IP literal, and the host itself when it *is* a public suffix (there is no
    /// registrable domain below it to record).
    /// </summary>
    public string? GetRegistrableDomain(string? host)
    {
        var normalized = Normalize(host);
        if (normalized is null)
        {
            return null;
        }

        var suffix = GetPublicSuffix(normalized);
        if (suffix is null)
        {
            return null;
        }

        if (string.Equals(normalized, suffix, StringComparison.Ordinal))
        {
            return normalized;
        }

        var suffixLabels = suffix.Split('.').Length;
        var labels = normalized.Split('.');
        return labels.Length <= suffixLabels ? normalized : Join(labels, labels.Length - suffixLabels - 1);
    }

    /// <summary>True when the value is an IPv4 or IPv6 literal rather than a name.</summary>
    public static bool IsIpLiteral(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        var parts = host.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3)
            {
                return false;
            }

            foreach (var ch in part)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Lower-cases and trims a host, and returns null for anything that is not a name we can reduce
    /// (empty, an IP literal, or a value with empty labels).
    /// </summary>
    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var value = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (value.Length == 0 || IsIpLiteral(value))
        {
            return null;
        }

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0)
            {
                return null;
            }
        }

        return value;
    }
}
