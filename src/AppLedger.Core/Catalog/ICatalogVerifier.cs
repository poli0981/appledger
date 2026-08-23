namespace AppLedger.Core.Catalog;

/// <summary>Why a catalog file was accepted or refused.</summary>
public enum CatalogVerifyResult
{
    /// <summary>Signature and global signature both check out against the embedded key.</summary>
    Valid,

    /// <summary>The signature names a different key id. Refused before any crypto runs.</summary>
    WrongKey,

    /// <summary>The signature does not cover this file.</summary>
    BadSignature,

    /// <summary>The trusted comment was tampered with.</summary>
    BadGlobalSignature,

    /// <summary>The file uses legacy <c>Ed</c> mode; docs/13 requires prehashed <c>ED</c>.</summary>
    UnsupportedAlgorithm,

    /// <summary>The <c>.minisig</c> file could not be parsed at all.</summary>
    Malformed,
}

/// <summary>
/// Verifies a detached minisign signature. The implementation needs Ed25519 and BLAKE2b-512 and therefore
/// lives in Infrastructure (NSec); Core keeps no crypto dependency and only parses the file formats.
/// </summary>
/// <remarks>
/// Even the catalog bundled in the install folder is verified, because that folder is user-writable
/// (docs/13_CATALOG_RULES.md §Signing &amp; verification, docs/11_SAFETY_POLICY.md §Privilege boundary).
/// </remarks>
public interface ICatalogVerifier
{
    /// <summary>The public key this verifier trusts, for display in Settings › Catalog.</summary>
    MinisignPublicKey TrustedKey { get; }

    /// <summary>
    /// Verifies <paramref name="fileContent"/> against <paramref name="signatureFileContent"/>.
    /// Never throws for bad input: a malformed signature is a result, not an exception.
    /// </summary>
    CatalogVerifyResult Verify(ReadOnlySpan<byte> fileContent, string signatureFileContent);
}
