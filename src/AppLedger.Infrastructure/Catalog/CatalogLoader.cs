using System.Globalization;
using System.Text;
using AppLedger.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace AppLedger.Infrastructure.Catalog;

/// <summary>Why a catalog was not loaded. Every value here means "the previous catalog is still active".</summary>
public enum CatalogLoadOutcome
{
    /// <summary>Verified, parsed and validated.</summary>
    Loaded,

    /// <summary>There is no trusted key embedded in this build, so nothing can be verified.</summary>
    NoTrustedKey,

    /// <summary>The catalog or its signature file is missing.</summary>
    Missing,

    /// <summary>The file is larger than the 4 MB cap of docs/11_SAFETY_POLICY.md §Privilege boundary.</summary>
    TooLarge,

    /// <summary>The signature did not check out. The specific reason is in <see cref="CatalogLoadResult.VerifyResult"/>.</summary>
    SignatureRejected,

    /// <summary>The signature was good but the document failed strict parsing or validation.</summary>
    Invalid,

    /// <summary>The document is older than the one already loaded. A catalog is never downgraded.</summary>
    NotNewer,
}

/// <summary>The outcome of one load attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Document">The loaded document, or null for every non-<see cref="CatalogLoadOutcome.Loaded"/> outcome.</param>
/// <param name="VerifyResult">The signature verdict, when verification ran.</param>
/// <param name="Error">A short reason, safe to log: it never contains a path or a rule body.</param>
public readonly record struct CatalogLoadResult(
    CatalogLoadOutcome Outcome,
    CatalogDocument? Document,
    CatalogVerifyResult? VerifyResult,
    string? Error)
{
    /// <summary>True only when a document was actually loaded.</summary>
    public bool Succeeded => Outcome == CatalogLoadOutcome.Loaded && Document is not null;
}

/// <summary>
/// Loads and verifies <c>appledger-catalog.json</c> (docs/13_CATALOG_RULES.md §Update flow).
/// </summary>
/// <remarks>
/// <b>Fails closed, always.</b> Every path out of <see cref="Load"/> that is not
/// <see cref="CatalogLoadOutcome.Loaded"/> leaves the previously active catalog in place and loads
/// nothing — including the case where this build has no trusted key embedded yet. docs/01_ARCHITECTURE.md
/// §Degraded modes puts it plainly: keep the last good catalog, never load unsigned data. An elevated
/// Agent that would rather have rules than have correct rules is the failure mode this whole file exists
/// to prevent.
/// </remarks>
public sealed partial class CatalogLoader
{
    /// <summary>The size cap from docs/11_SAFETY_POLICY.md §Privilege boundary.</summary>
    public const int MaxCatalogBytes = 4 * 1024 * 1024;

    private readonly ICatalogVerifier _verifier;
    private readonly EnvExpander _expander;
    private readonly ILogger<CatalogLoader> _logger;

    /// <summary>Creates a loader.</summary>
    /// <param name="verifier">The signature verifier bound to the trusted key.</param>
    /// <param name="expander">Environment values for the catalog's globs, from the known folders.</param>
    /// <param name="logger">Structured log sink; nothing logged here carries a path or a rule body.</param>
    public CatalogLoader(ICatalogVerifier verifier, EnvExpander expander, ILogger<CatalogLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(expander);
        ArgumentNullException.ThrowIfNull(logger);

        _verifier = verifier;
        _expander = expander;
        _logger = logger;
    }

    /// <summary>
    /// Builds a loader over the key embedded in this build, or returns null when no key has been embedded
    /// yet. A null return is not an error to work around: it means this build cannot trust any catalog,
    /// and the Agent must run on the built-in minimum until a release key exists.
    /// </summary>
    public static CatalogLoader? TryCreateFromEmbeddedKey(EnvExpander expander, ILogger<CatalogLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!CatalogPublicKey.TryGetEmbedded(out var key))
        {
            LogNoTrustedKey(logger);
            return null;
        }

        return new CatalogLoader(new MinisignVerifier(key), expander, logger);
    }

    /// <summary>
    /// Verifies and parses a catalog file and its detached signature.
    /// </summary>
    /// <param name="catalogPath">The <c>.json</c> file.</param>
    /// <param name="signaturePath">The <c>.minisig</c> beside it.</param>
    /// <param name="active">The currently active document, so a downgrade can be refused.</param>
    public CatalogLoadResult Load(string catalogPath, string signaturePath, CatalogDocument? active = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);

        if (!File.Exists(catalogPath) || !File.Exists(signaturePath))
        {
            return Reject(CatalogLoadOutcome.Missing, "The catalog or its signature is not present.");
        }

        var length = new FileInfo(catalogPath).Length;
        if (length > MaxCatalogBytes)
        {
            // Checked before reading, so an oversized file is never brought into memory.
            return Reject(
                CatalogLoadOutcome.TooLarge,
                string.Create(CultureInfo.InvariantCulture, $"The catalog is {length} bytes, over the {MaxCatalogBytes} cap."));
        }

        byte[] content;
        string signatureText;
        try
        {
            content = File.ReadAllBytes(catalogPath);
            signatureText = File.ReadAllText(signaturePath);
        }
        catch (IOException ex)
        {
            return Reject(CatalogLoadOutcome.Missing, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Reject(CatalogLoadOutcome.Missing, ex.Message);
        }

        var verdict = _verifier.Verify(content, signatureText);
        if (verdict != CatalogVerifyResult.Valid)
        {
            LogSignatureRejected(_logger, verdict);
            return new CatalogLoadResult(CatalogLoadOutcome.SignatureRejected, null, verdict, verdict.ToString());
        }

        // Strict parsing is a feature: an unknown field rejects the document, because a silent typo in
        // identity rules is worse than a failed update (docs/13_CATALOG_RULES.md).
        if (!CatalogParser.TryParse(DecodeUtf8(content), _expander, out var document, out var error))
        {
            LogParseRejected(_logger, error);
            return new CatalogLoadResult(CatalogLoadOutcome.Invalid, null, verdict, error);
        }

        if (active is not null && CatalogParser.CompareVersions(document!.Version, active.Version) <= 0)
        {
            return new CatalogLoadResult(CatalogLoadOutcome.NotNewer, null, verdict, "Not newer than the active catalog.");
        }

        LogLoaded(_logger, document!.Version);
        return new CatalogLoadResult(CatalogLoadOutcome.Loaded, document, verdict, null);
    }

    private CatalogLoadResult Reject(CatalogLoadOutcome outcome, string error)
    {
        LogNotLoaded(_logger, outcome);
        return new CatalogLoadResult(outcome, null, null, error);
    }

    // Source-generated log methods (CA1848). Nothing here carries a path, a host or a rule body, so every
    // one of them is safe at Information level under docs/15_LOGGING.md Redaction.
    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "No catalog signing key is embedded in this build; catalog rules are disabled and the built-in policy minimum applies.")]
    private static partial void LogNoTrustedKey(ILogger logger);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Catalog signature rejected: {Reason}.")]
    private static partial void LogSignatureRejected(ILogger logger, CatalogVerifyResult reason);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "Catalog rejected by the strict parser: {Reason}.")]
    private static partial void LogParseRejected(ILogger logger, string? reason);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Information, Message = "Catalog {Version} loaded and verified.")]
    private static partial void LogLoaded(ILogger logger, string version);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "Catalog not loaded: {Outcome}.")]
    private static partial void LogNotLoaded(ILogger logger, CatalogLoadOutcome outcome);

    /// <summary>
    /// Decodes as UTF-8, tolerating a byte-order mark. A BOM is legal in a JSON file and would otherwise
    /// become an unexpected character at offset zero.
    /// </summary>
    private static string DecodeUtf8(byte[] content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? Encoding.UTF8.GetString(content, 3, content.Length - 3)
            : Encoding.UTF8.GetString(content);
}
