using System.Globalization;
using AppLedger.Core.Policy;

namespace AppLedger.Core.Format;

/// <summary>
/// Turns a path into a class description that keeps the shape and loses the names, for log events at
/// Information and above (docs/15_LOGGING.md §Redaction).
/// </summary>
/// <remarks>
/// A log line must be useful for a bug report and useless as a record of what the user did. Depth and
/// extension answer "which kind of thing was this"; the names would answer "what was it".
/// </remarks>
public static class PathRedactor
{
    /// <summary>
    /// Classifies a path against a set of known roots, e.g. <c>&lt;install-root&gt;\…\.dll</c> or
    /// <c>&lt;userprofile&gt;\…</c>. Returns <c>&lt;none&gt;</c> for null or empty input.
    /// </summary>
    /// <param name="path">The path to classify. Never appears in the result.</param>
    /// <param name="knownRoots">
    /// Label to root mapping, most specific first, e.g. <c>{ "install-root", @"C:\Program Files\X" }</c>.
    /// </param>
    public static string ToClass(string? path, IReadOnlyList<KeyValuePair<string, string>>? knownRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<none>";
        }

        var label = "<drive>";
        var remainder = path;

        if (knownRoots is not null)
        {
            foreach (var (name, root) in knownRoots)
            {
                if (!PathRules.IsUnder(path, root))
                {
                    continue;
                }

                label = "<" + name + ">";
                remainder = path.Length > root.Length ? path[root.Length..] : string.Empty;
                break;
            }
        }

        var depth = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        var extension = ExtensionOf(path);

        return depth == 0
            ? label
            : string.Create(CultureInfo.InvariantCulture, $"{label}\\…({depth}){extension}");
    }

    /// <summary>The lower-cased extension including the dot, or an empty string when there is none.</summary>
    private static string ExtensionOf(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var slash = trimmed.LastIndexOf('\\');
        var name = slash < 0 ? trimmed : trimmed[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
    }
}

/// <summary>
/// Turns a hostname or address into a class token for logs. At Information the value never appears; at
/// Debug the caller logs the value directly (docs/15_LOGGING.md §Redaction).
/// </summary>
public static class HostRedactor
{
    /// <summary>
    /// Classifies a host: <c>&lt;ip-v4&gt;</c>, <c>&lt;ip-v6&gt;</c>, <c>&lt;etld1&gt;</c> or
    /// <c>&lt;host&gt;</c>. Deliberately does not reveal the registrable domain itself.
    /// </summary>
    public static string ToClass(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "<none>";
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return "<ip-v6>";
        }

        if (LooksLikeIpv4(host))
        {
            return "<ip-v4>";
        }

        return host.Contains('.', StringComparison.Ordinal) ? "<etld1>" : "<host>";
    }

    private static bool LooksLikeIpv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }
}
