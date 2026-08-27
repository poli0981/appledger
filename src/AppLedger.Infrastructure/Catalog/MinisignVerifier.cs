using AppLedger.Core.Catalog;
using NSec.Cryptography;

namespace AppLedger.Infrastructure.Catalog;

/// <summary>
/// Verifies a detached minisign signature with Ed25519 over a BLAKE2b-512 prehash
/// (docs/13_CATALOG_RULES.md §Signing &amp; verification).
/// </summary>
/// <remarks>
/// Parsing lives in <see cref="MinisignSignature"/> in Core, which keeps no crypto dependency; only the
/// two primitives need NSec and therefore live here (ADR-12). The order of the checks below is part of the
/// contract: a signature from an unrelated key is refused on its key id, before any verification is spent.
/// </remarks>
public sealed class MinisignVerifier : ICatalogVerifier
{
    private readonly PublicKey _key;

    /// <summary>Creates a verifier that trusts exactly one key.</summary>
    public MinisignVerifier(MinisignPublicKey trustedKey)
    {
        ArgumentNullException.ThrowIfNull(trustedKey);
        TrustedKey = trustedKey;
        _key = PublicKey.Import(SignatureAlgorithm.Ed25519, [.. trustedKey.PublicKey], KeyBlobFormat.RawPublicKey);
    }

    /// <inheritdoc />
    public MinisignPublicKey TrustedKey { get; }

    /// <inheritdoc />
    public CatalogVerifyResult Verify(ReadOnlySpan<byte> fileContent, string signatureFileContent)
    {
        if (!MinisignSignature.TryParse(signatureFileContent ?? string.Empty, out var signature, out _))
        {
            return CatalogVerifyResult.Malformed;
        }

        // docs/13 requires the prehashed form. The legacy mode streams the whole file into Ed25519, which
        // means the verifier has to buffer an attacker-controlled length before it can reject anything.
        if (signature.Algorithm != MinisignAlgorithm.Prehashed)
        {
            return CatalogVerifyResult.UnsupportedAlgorithm;
        }

        if (!signature.MatchesKey(TrustedKey))
        {
            return CatalogVerifyResult.WrongKey;
        }

        Span<byte> digest = stackalloc byte[HashAlgorithm.Blake2b_512.HashSize];
        HashAlgorithm.Blake2b_512.Hash(fileContent, digest);

        if (!SignatureAlgorithm.Ed25519.Verify(_key, digest, [.. signature.Signature]))
        {
            return CatalogVerifyResult.BadSignature;
        }

        // The trusted comment is only trustworthy because this second signature covers it. Skipping the
        // check would leave a field that looks authoritative and is not.
        return SignatureAlgorithm.Ed25519.Verify(_key, signature.GlobalSignedData(), [.. signature.GlobalSignature])
            ? CatalogVerifyResult.Valid
            : CatalogVerifyResult.BadGlobalSignature;
    }
}
