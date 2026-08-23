using System.Text;

namespace AppLedger.Core.Catalog;

/// <summary>
/// Expands the <c>%VAR%</c> tokens a catalog glob may use. Only the allow-list of
/// docs/13_CATALOG_RULES.md is honoured: a signed rules file must not be able to name an arbitrary
/// environment variable and have the elevated Agent resolve it.
/// </summary>
/// <remarks>
/// The values are injected rather than read from the process environment, so Core stays OS-agnostic and
/// the catalog tests are deterministic on any machine (docs/19_TESTING.md).
/// </remarks>
public sealed class EnvExpander
{
    /// <summary>The only variable names a catalog may reference.</summary>
    public static IReadOnlySet<string> AllowedVariables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LOCALAPPDATA",
        "APPDATA",
        "USERPROFILE",
        "PROGRAMDATA",
        "PROGRAMFILES",
        "PROGRAMFILES(X86)",
        "PUBLIC",
        "TEMP",
    };

    private readonly Dictionary<string, string> _values;

    /// <summary>Creates an expander over a variable-name to path mapping.</summary>
    /// <param name="values">
    /// Values for (a subset of) <see cref="AllowedVariables"/>. Names outside the allow-list are ignored
    /// rather than trusted.
    /// </param>
    public EnvExpander(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (AllowedVariables.Contains(name))
            {
                _values[name] = value.TrimEnd('\\');
            }
        }
    }

    /// <summary>
    /// A fixed set of values suitable for tests and for validating a catalog on a machine whose real
    /// folders are irrelevant — the schema test only cares whether a pattern *can* be rooted.
    /// </summary>
    public static EnvExpander ForValidation { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LOCALAPPDATA"] = @"C:\Users\fixture\AppData\Local",
        ["APPDATA"] = @"C:\Users\fixture\AppData\Roaming",
        ["USERPROFILE"] = @"C:\Users\fixture",
        ["PROGRAMDATA"] = @"C:\ProgramData",
        ["PROGRAMFILES"] = @"C:\Program Files",
        ["PROGRAMFILES(X86)"] = @"C:\Program Files (x86)",
        ["PUBLIC"] = @"C:\Users\Public",
        ["TEMP"] = @"C:\Users\fixture\AppData\Local\Temp",
    });

    /// <summary>
    /// Expands every <c>%VAR%</c> token. Throws <see cref="FormatException"/> for a variable outside the
    /// allow-list or one this expander has no value for, so a rules file cannot smuggle in an unexpanded
    /// token that a later path comparison would treat as a literal directory name.
    /// </summary>
    public string Expand(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (!pattern.Contains('%', StringComparison.Ordinal))
        {
            return pattern;
        }

        var sb = new StringBuilder(pattern.Length + 32);
        var i = 0;

        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('%', i);
            if (open < 0)
            {
                sb.Append(pattern, i, pattern.Length - i);
                break;
            }

            var close = pattern.IndexOf('%', open + 1);
            if (close < 0)
            {
                throw new FormatException($"Unterminated %VAR% token in '{pattern}'.");
            }

            sb.Append(pattern, i, open - i);
            var name = pattern[(open + 1)..close];

            if (!AllowedVariables.Contains(name))
            {
                throw new FormatException(
                    $"'%{name}%' is not in the catalog environment allow-list (docs/13_CATALOG_RULES.md §Glob grammar).");
            }

            if (!_values.TryGetValue(name, out var value))
            {
                throw new FormatException($"No value supplied for '%{name}%'.");
            }

            sb.Append(value);
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>Expands and parses in one step, the shape every catalog glob goes through.</summary>
    public PathGlob ExpandToGlob(string pattern) => PathGlob.Parse(Expand(pattern));
}
