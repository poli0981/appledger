using System.Globalization;
using System.Text.Json;

namespace AppLedger.Core.Catalog;

/// <summary>Thrown when a catalog file is rejected. The message names the exact rule that failed.</summary>
public sealed class CatalogException : Exception
{
    /// <summary>Creates the exception with a message describing the failed rule.</summary>
    public CatalogException(string message) : base(message) { }

    /// <summary>Creates the exception wrapping the underlying parse failure.</summary>
    public CatalogException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Parameterless constructor, present only to satisfy the exception design guideline.</summary>
    public CatalogException() { }
}

/// <summary>
/// Strict reader for <c>appledger-catalog.json</c>. Everything about it is deliberately unforgiving: an
/// unknown field, an unknown rule kind, a duplicate id or an unrooted glob rejects the whole file and the
/// Agent keeps the last good catalog (docs/13_CATALOG_RULES.md §Strict parsing).
/// </summary>
public static class CatalogParser
{
    /// <summary>The schema version this build understands. A newer file is refused, never downgraded.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Hard size cap. A rules file is data, and 4 MB is far beyond any legitimate one.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    /// <summary>
    /// Parses and validates a catalog. Throws <see cref="CatalogException"/> for anything that fails, so
    /// the caller can log one message and keep the previous file.
    /// </summary>
    /// <param name="json">The file contents.</param>
    /// <param name="expander">Environment values used to check that every glob is rooted.</param>
    public static CatalogDocument Parse(string json, EnvExpander? expander = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Count bytes rather than chars: the cap is about the file we downloaded.
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new CatalogException($"Catalog exceeds the {MaxBytes / (1024 * 1024)} MB cap.");
        }

        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new CatalogException($"Catalog is not valid against the schema: {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new CatalogException("Catalog is empty.");
        }

        CatalogValidator.Validate(document, expander ?? EnvExpander.ForValidation);
        return document;
    }

    /// <summary>Parses without throwing; <paramref name="error"/> carries the reason on failure.</summary>
    public static bool TryParse(string json, EnvExpander? expander, out CatalogDocument? document, out string? error)
    {
        try
        {
            document = Parse(json, expander);
            error = null;
            return true;
        }
        catch (CatalogException ex)
        {
            document = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Compares two CalVer <c>YYYY.MM.N</c> versions. Returns a negative number when <paramref name="left"/>
    /// is older. Used to enforce "an older catalog never replaces a newer one".
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var a = ParseCalVer(left);
        var b = ParseCalVer(right);

        var byYear = a.Year.CompareTo(b.Year);
        if (byYear != 0)
        {
            return byYear;
        }

        var byMonth = a.Month.CompareTo(b.Month);
        return byMonth != 0 ? byMonth : a.Serial.CompareTo(b.Serial);
    }

    /// <summary>Parses a CalVer <c>YYYY.MM.N</c> string.</summary>
    public static (int Year, int Month, int Serial) ParseCalVer(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = version.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var serial)
            || month is < 1 or > 12)
        {
            throw new CatalogException($"Catalog version '{version}' is not CalVer YYYY.MM.N.");
        }

        return (year, month, serial);
    }
}
