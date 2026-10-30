using System.Text;

namespace AppLedger.Infrastructure.Identity;

/// <summary>
/// Just enough of Valve's KeyValues text format to read <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>.
/// </summary>
/// <remarks>
/// Deliberately not a parser. Both files are read for a handful of named scalars — a library's
/// <c>path</c>, an app's <c>appid</c> and <c>installdir</c> — each of which appears once per object, so a
/// tokenizer that yields every quoted key/value pair it meets answers both questions without a tree, and
/// without a dependency on a format Valve has changed before (<c>libraryfolders.vdf</c> went from a flat
/// index-to-path map to nested objects, which is exactly the kind of change a flat scan survives).
/// <para>
/// Keys are returned lower-cased because the files are not consistent about case across Steam versions.
/// </para>
/// </remarks>
internal static class ValveKeyValues
{
    /// <summary>
    /// Every scalar <c>"key" "value"</c> pair in the text, in order, with keys lower-cased. A key that
    /// opens an object yields nothing of its own.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Scalars(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? pendingKey = null;

        foreach (var token in Tokens(text))
        {
            if (token.IsBrace)
            {
                // An object opened or closed, so whatever key preceded it was its name, not a scalar's.
                pendingKey = null;
                continue;
            }

            if (pendingKey is null)
            {
                pendingKey = token.Text.ToLowerInvariant();
                continue;
            }

            yield return new KeyValuePair<string, string>(pendingKey, token.Text);
            pendingKey = null;
        }
    }

    /// <summary>The first value for a key, or null. Comparison is on the lower-cased key.</summary>
    public static string? Value(string text, string key)
    {
        foreach (var pair in Scalars(text))
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Every value for a key, which is how a nested list of library paths is read.</summary>
    public static IEnumerable<string> Values(string text, string key)
    {
        foreach (var pair in Scalars(text))
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                yield return pair.Value;
            }
        }
    }

    private static IEnumerable<Token> Tokens(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch is '{' or '}')
            {
                yield return new Token(string.Empty, IsBrace: true);
                continue;
            }

            // `//` to end of line. KeyValues has no block comments.
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                {
                    i++;
                }

                continue;
            }

            if (ch != '"')
            {
                continue;
            }

            var builder = new StringBuilder();
            i++;

            while (i < text.Length && text[i] != '"')
            {
                // The only escapes these files use are \\ for a path separator and \" inside a value.
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    builder.Append(text[i + 1] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        var other => other,
                    });
                    i += 2;
                    continue;
                }

                builder.Append(text[i]);
                i++;
            }

            yield return new Token(builder.ToString(), IsBrace: false);
        }
    }

    private readonly record struct Token(string Text, bool IsBrace);
}
