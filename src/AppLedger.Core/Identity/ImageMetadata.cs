namespace AppLedger.Core.Identity;

/// <summary>
/// The Authenticode verdict stored in <c>apps.sig_status</c> (docs/06_DATA_MODEL.md).
/// </summary>
/// <remarks>
/// This is a *status*, never a safety verdict. AppLedger does not answer "is this app safe?"
/// (docs/04_DATA_SOURCES.md §H); it reports what Windows says about the signature and lets the user decide.
/// </remarks>
public enum SignatureStatus
{
    /// <summary>Not determined — the file could not be read, or verification was not attempted.</summary>
    Unknown = 0,

    /// <summary>An embedded signature that chains to a trusted root and has not expired.</summary>
    Valid,

    /// <summary>An embedded signature whose certificate has expired and that carries no valid timestamp.</summary>
    Expired,

    /// <summary>An embedded signature that does not chain to a trusted root, or that Windows rejects.</summary>
    Untrusted,

    /// <summary>No signature at all.</summary>
    // CA1720 reads "Unsigned" as the numeric type. It is the value stored in apps.sig_status
    // (docs/06_DATA_MODEL.md); renaming it would desynchronise the enum from the column it serialises into.
#pragma warning disable CA1720
    Unsigned,
#pragma warning restore CA1720

    /// <summary>
    /// Signed through a Windows security catalog rather than embedded in the file. Tier-0 files are
    /// reported this way without computing catalog hashes (docs/03_APP_IDENTITY.md §Metadata enrichment).
    /// </summary>
    CatalogSigned,
}

/// <summary>
/// What a PE file says about itself, plus what Windows says about its signature. Read once per
/// <c>(app_id, version)</c> (docs/03_APP_IDENTITY.md §Metadata enrichment).
/// </summary>
public readonly record struct ImageMetadata
{
    /// <summary>Nothing could be read about this file.</summary>
    public static ImageMetadata Empty => default;

    /// <summary>PE <c>ProductName</c>.</summary>
    public string? ProductName { get; init; }

    /// <summary>PE <c>FileDescription</c>. Often the friendliest display name a file carries.</summary>
    public string? FileDescription { get; init; }

    /// <summary>PE <c>CompanyName</c>.</summary>
    public string? CompanyName { get; init; }

    /// <summary>PE <c>ProductVersion</c>, as written — it is a free-text field, not necessarily numeric.</summary>
    public string? ProductVersion { get; init; }

    /// <summary>PE <c>FileVersion</c>, as written.</summary>
    public string? FileVersion { get; init; }

    /// <summary>PE <c>LegalCopyright</c>.</summary>
    public string? LegalCopyright { get; init; }

    /// <summary>The signature status Windows reports.</summary>
    public SignatureStatus SignatureStatus { get; init; }

    /// <summary>
    /// The signing certificate's subject common name, when there is an embedded signature to read it from.
    /// Null for <see cref="SignatureStatus.CatalogSigned"/> and <see cref="SignatureStatus.Unsigned"/>.
    /// </summary>
    public string? Signer { get; init; }
}

/// <summary>Reads PE version information and the Authenticode status of an image on disk.</summary>
/// <remarks>
/// Verification is deliberately offline: <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> and
/// <c>WTD_REVOCATION_CHECK_NONE</c>, because a revocation check would be a network call, and
/// docs/12_PRIVACY_AND_RETENTION.md §Network calls is an exhaustive list that does not include one.
/// </remarks>
public interface IImageMetadataReader
{
    /// <summary>
    /// Reads what can be read. Never throws for a missing, locked or malformed file: the affected fields
    /// come back null and the status comes back <see cref="SignatureStatus.Unknown"/>.
    /// </summary>
    /// <param name="canonicalImagePath">A canonical path that has already been through the policy.</param>
    ImageMetadata Read(string canonicalImagePath);
}
