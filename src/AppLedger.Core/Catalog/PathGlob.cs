namespace AppLedger.Core.Catalog;

/// <summary>
/// A rooted path glob as used by <c>install_root_glob</c>, <c>data_dirs</c>, <c>cache_dirs</c>,
/// <c>sensitive_paths</c> and <c>protected_paths</c> (docs/13_CATALOG_RULES.md §Glob grammar).
/// </summary>
/// <remarks>
/// The grammar is deliberately small: <c>*</c> matches inside one path component, <c>**</c> spans
/// components, <c>?</c> matches one character, and the pattern must be rooted — either drive-absolute or
/// starting with the drive-wildcard token <c>?:\</c>. An unrooted glob is rejected at parse time so a
/// typo cannot silently widen a rule to every directory with a matching name.
/// </remarks>
public sealed class PathGlob
{
    private readonly string[] _segments;

    private PathGlob(string pattern, char? drive, bool anyDrive, string[] segments)
    {
        Pattern = pattern;
        Drive = drive;
        AnyDrive = anyDrive;
        _segments = segments;
    }

    /// <summary>The pattern as written, after environment expansion.</summary>
    public string Pattern { get; }

    /// <summary>The fixed drive letter, upper-cased, or null when <see cref="AnyDrive"/> is true.</summary>
    public char? Drive { get; }

    /// <summary>True for a <c>?:\</c> pattern, which matches the same path on any drive.</summary>
    public bool AnyDrive { get; }

    /// <summary>
    /// Parses an already environment-expanded pattern. Throws <see cref="FormatException"/> when it is not
    /// rooted, which is what the catalog schema test asserts.
    /// </summary>
    public static PathGlob Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var normalized = pattern.Replace('/', '\\');

        bool anyDrive;
        char? drive;
        string rest;

        if (normalized.StartsWith(@"?:\", StringComparison.Ordinal))
        {
            anyDrive = true;
            drive = null;
            rest = normalized[3..];
        }
        else if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            anyDrive = false;
            drive = char.ToUpperInvariant(normalized[0]);
            rest = normalized[3..];
        }
        else
        {
            throw new FormatException(
                $"Glob '{pattern}' is not rooted. After %VAR% expansion a glob must start with a drive letter or the "
                + @"drive-wildcard token '?:\' (docs/13_CATALOG_RULES.md §Glob grammar).");
        }

        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return new PathGlob(normalized, drive, anyDrive, segments);
    }

    /// <summary>Parses without throwing.</summary>
    public static bool TryParse(string pattern, out PathGlob? glob)
    {
        try
        {
            glob = Parse(pattern);
            return true;
        }
        catch (FormatException)
        {
            glob = null;
            return false;
        }
        catch (ArgumentException)
        {
            glob = null;
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="canonicalPath"/> matches. A pattern with no trailing <c>**</c> matches the
    /// directory itself, not its contents — use <see cref="MatchesOrContains"/> when a rule is meant to
    /// cover a whole subtree.
    /// </summary>
    public bool IsMatch(string? canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath) || canonicalPath.Length < 3)
        {
            return false;
        }

        if (!DriveMatches(canonicalPath[0]))
        {
            return false;
        }

        var parts = canonicalPath[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(0, parts, 0);
    }

    /// <summary>
    /// True when the path matches the pattern or lies beneath a directory that does. This is the shape most
    /// catalog rules want: <c>%APPDATA%\discord</c> should cover everything inside it.
    /// </summary>
    public bool MatchesOrContains(string? canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath))
        {
            return false;
        }

        if (IsMatch(canonicalPath))
        {
            return true;
        }

        // Walk up: any ancestor matching the pattern means the path is inside a matched directory.
        var current = canonicalPath;
        while (true)
        {
            var slash = current.LastIndexOf('\\');
            if (slash <= 2)
            {
                return false;
            }

            current = current[..slash];
            if (IsMatch(current))
            {
                return true;
            }
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Pattern;

    private bool DriveMatches(char pathDrive) =>
        AnyDrive ? char.IsAsciiLetter(pathDrive) : char.ToUpperInvariant(pathDrive) == Drive;

    private bool MatchSegments(int patternIndex, string[] parts, int partIndex)
    {
        while (true)
        {
            if (patternIndex == _segments.Length)
            {
                return partIndex == parts.Length;
            }

            var segment = _segments[patternIndex];

            if (segment == "**")
            {
                // A trailing ** matches the rest, including nothing.
                if (patternIndex == _segments.Length - 1)
                {
                    return true;
                }

                for (var skip = partIndex; skip <= parts.Length; skip++)
                {
                    if (MatchSegments(patternIndex + 1, parts, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (partIndex == parts.Length || !WildcardMatch(segment, parts[partIndex]))
            {
                return false;
            }

            patternIndex++;
            partIndex++;
        }
    }

    /// <summary>
    /// Case-insensitive <c>*</c>/<c>?</c> matching inside a single path component. Public because the same
    /// grammar backs <c>host_rules[].exe_glob</c>, which matches bare executable names.
    /// </summary>
    public static bool WildcardMatch(string pattern, string value)
    {
        int p = 0, v = 0, starP = -1, starV = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
