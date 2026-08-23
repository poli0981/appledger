using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AppLedger.Core.Policy;

/// <summary>
/// The parts of docs/11_SAFETY_POLICY.md §Canonicalization that are pure string work: rejection of
/// unusable shapes (step 1), lexical normalization (step 2), alternate-data-stream stripping (step 4),
/// and the containment comparison (step 5).
/// </summary>
/// <remarks>
/// Steps 2b (8.3 expansion) and 3 (<c>GetFinalPathNameByHandleW</c>) need the file system and live in
/// the Infrastructure adapter behind <see cref="IPolicyGuard"/>. Keeping the rest here means the whole
/// rejection and containment table is testable on any OS with no privileges (docs/19_TESTING.md).
/// </remarks>
public static class PathRules
{
    /// <summary>The longest path we will consider, matching Windows' long-path ceiling.</summary>
    public const int MaxPathLength = 32767;

    /// <summary>
    /// Lexically normalizes a rooted Windows path: expands the extended-length prefix, collapses separators,
    /// resolves <c>.</c> and <c>..</c>, trims trailing dots and spaces from each component, and strips any
    /// alternate-data-stream suffix. Returns false with a reason for anything we refuse to handle.
    /// </summary>
    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string? normalized, out PathDenyReason reason)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = PathDenyReason.Empty;
            return false;
        }

        if (raw.Length > MaxPathLength)
        {
            reason = PathDenyReason.TooLong;
            return false;
        }

        var path = raw.Replace('/', '\\');

        // No network paths in v1, in either spelling.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            reason = PathDenyReason.NetworkPath;
            return false;
        }

        // Strip the extended-length prefix before anything else: it contains a '?', which is otherwise an
        // illegal path character, and `\\?\C:\x` and the same path mean the same thing.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];

            if (path.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                reason = PathDenyReason.DevicePath;
                return false;
            }
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            reason = PathDenyReason.DevicePath;
            return false;
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = PathDenyReason.NetworkPath;
            return false;
        }

        // Wildcards and reserved characters are only illegal once the prefix is gone.
        foreach (var ch in path)
        {
            if (char.IsControl(ch) || ch is '<' or '>' or '|' or '*' or '?')
            {
                reason = PathDenyReason.InvalidCharacters;
                return false;
            }
        }

        path = StripAlternateDataStream(path);

        if (!IsDriveRooted(path))
        {
            reason = PathDenyReason.NotRooted;
            return false;
        }

        var root = string.Concat(char.ToUpperInvariant(path[0]), @":\");
        var rest = path.Length > 3 ? path[3..] : string.Empty;

        var stack = new List<string>();
        foreach (var rawSegment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            // '.' and '..' must be recognised before trailing dots are trimmed, or '..' would trim to
            // nothing and a traversal would silently be dropped instead of resolved.
            if (rawSegment == ".")
            {
                continue;
            }

            if (rawSegment == "..")
            {
                // Windows clamps at the volume root rather than failing.
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            var segment = TrimTrailingDotsAndSpaces(rawSegment);
            if (segment.Length == 0)
            {
                continue;
            }

            stack.Add(segment);
        }

        normalized = stack.Count == 0 ? root : root + string.Join('\\', stack);
        reason = PathDenyReason.None;
        return true;
    }

    /// <summary>
    /// Removes an alternate-data-stream suffix (<c>file.txt:stream</c>) and returns the underlying path.
    /// Streams are never enumerated, so the stream name is dropped rather than carried around.
    /// </summary>
    public static string StripAlternateDataStream(string path)
    {
        // The drive-letter colon at index 1 is not a stream separator.
        var colon = path.IndexOf(':', 2);
        return colon < 0 ? path : path[..colon];
    }

    /// <summary>
    /// Trims the trailing dots and spaces Windows silently drops, so <c>"Temp. "</c> and <c>"Temp"</c> are
    /// not two different directories to us either.
    /// </summary>
    public static string TrimTrailingDotsAndSpaces(string segment) => segment.TrimEnd(' ', '.');

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies beneath it.
    /// Comparison is ordinal and case-insensitive with a trailing separator, so <c>C:\WindowsFoo</c> is
    /// not under <c>C:\Windows</c> (docs/11 §Canonicalization, step 5).
    /// </summary>
    public static bool IsUnder(string? candidate, string? root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var c = candidate.TrimEnd('\\');
        var r = root.TrimEnd('\\');

        if (c.Length < r.Length)
        {
            return false;
        }

        if (!c.StartsWith(r, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return c.Length == r.Length || c[r.Length] == '\\';
    }

    /// <summary>
    /// The last directory component of a normalized path, or null for a volume root. Used by the
    /// install-root heuristic and by the FileIO directory aggregation.
    /// </summary>
    public static string? LeafName(string normalizedPath)
    {
        var trimmed = normalizedPath.TrimEnd('\\');
        var slash = trimmed.LastIndexOf('\\');
        return slash < 0 || slash == 2 && trimmed.Length == 3 ? null : trimmed[(slash + 1)..];
    }

    /// <summary>The parent of a normalized path, or null when it is already a volume root.</summary>
    public static string? Parent(string normalizedPath)
    {
        var trimmed = normalizedPath.TrimEnd('\\');
        if (trimmed.Length <= 2)
        {
            return null;
        }

        var slash = trimmed.LastIndexOf('\\');
        if (slash < 0)
        {
            return null;
        }

        return slash == 2 ? trimmed[..3] : trimmed[..slash];
    }

    /// <summary>Ordinal, case-insensitive equality for two normalized paths.</summary>
    public static bool SamePath(string? left, string? right) =>
        string.Equals(left?.TrimEnd('\\'), right?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The volume root of a normalized path, e.g. <c>C:\</c>.</summary>
    public static string? VolumeRoot(string normalizedPath) =>
        IsDriveRooted(normalizedPath) ? string.Concat(char.ToUpperInvariant(normalizedPath[0]), @":\") : null;

    private static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>
    /// Renders a normalized path back with the extended-length prefix, for the file-system calls that need
    /// it. Kept here so the prefix is written in exactly one place.
    /// </summary>
    public static string ToExtendedLength(string normalizedPath) =>
        normalizedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? normalizedPath : @"\\?\" + normalizedPath;

    /// <summary>
    /// Lower-cases a normalized path for use as a dictionary key or hash input, without touching the
    /// stored form. Uses invariant casing so the key does not change with the user's locale.
    /// </summary>
    public static string ToComparisonKey(string normalizedPath)
    {
        var sb = new StringBuilder(normalizedPath.Length);
        foreach (var ch in normalizedPath.TrimEnd('\\'))
        {
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}
