using System.Diagnostics.CodeAnalysis;
using AppLedger.Core.Catalog;

namespace AppLedger.Infrastructure.Catalog;

/// <summary>
/// The one public key AppLedger trusts to sign the catalog (docs/13_CATALOG_RULES.md §Signing &amp;
/// verification). Embedded in the binary rather than read from disk, because the data root and the install
/// folder are both user-writable.
/// </summary>
/// <remarks>
/// The matching secret key exists only as the <c>CATALOG_MINISIGN_KEY</c> GitHub Actions secret
/// (docs/18_CI_CD.md) and never in this repository. Rotation embeds the new public key in an app update
/// first, then signs with both for one release cycle — a key swapped in the other order would reject every
/// catalog an already-installed build has.
/// </remarks>
public static class CatalogPublicKey
{
    /// <summary>
    /// The value the constant held before a real key existed. Kept so
    /// <see cref="TryParse(string, out MinisignPublicKey?)"/> can still recognise an unsubstituted build
    /// and refuse it: a build that forgot the key must fail closed, not verify against something that
    /// happens to decode.
    /// </summary>
    public const string Placeholder = "{{CATALOG_PUBKEY}}";

    /// <summary>
    /// The embedded minisign public key, verbatim from <c>minisign.pub</c>. Key id
    /// <c>6ED9A5D305231FDB</c>, which is what Settings › Catalog shows the user.
    /// </summary>
    public const string Embedded =
        "untrusted comment: minisign public key 6ED9A5D305231FDB\n"
        + "RWTbHyMF06XZbhmcgGC9Wp3u98TDwa2s0bySiAsPn9kuv4JiuRPXtqyc\n";

    /// <summary>True once a real key has been embedded.</summary>
    public static bool IsResolved => !string.Equals(Embedded.Trim(), Placeholder, StringComparison.Ordinal);

    /// <summary>
    /// Parses the embedded key. Returns false for an unsubstituted build and for text that is not a valid
    /// minisign public key — both of which mean the same thing to a caller: there is no key to verify
    /// against, so nothing may be loaded.
    /// </summary>
    public static bool TryGetEmbedded([NotNullWhen(true)] out MinisignPublicKey? key) => TryParse(Embedded, out key);

    /// <summary>
    /// The parse behind <see cref="TryGetEmbedded"/>, exposed so the fail-closed branches are testable
    /// without rebuilding the assembly with a different constant.
    /// </summary>
    public static bool TryParse(string? embedded, [NotNullWhen(true)] out MinisignPublicKey? key)
    {
        key = null;

        if (string.IsNullOrWhiteSpace(embedded) || string.Equals(embedded.Trim(), Placeholder, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            key = MinisignSignature.ParsePublicKey(embedded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
