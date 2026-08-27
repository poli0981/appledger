using System.Diagnostics.CodeAnalysis;
using AppLedger.Core.Catalog;

namespace AppLedger.Infrastructure.Catalog;

/// <summary>
/// The one public key AppLedger trusts to sign the catalog (docs/13_CATALOG_RULES.md §Signing &amp;
/// verification). Embedded in the binary rather than read from disk, because the data root and the install
/// folder are both user-writable.
/// </summary>
/// <remarks>
/// <b>The key does not exist yet.</b> <c>{{CATALOG_PUBKEY}}</c> is filled in at the first release, when
/// the matching secret key is created as the <c>CATALOG_MINISIGN_KEY</c> GitHub secret
/// (docs/18_CI_CD.md). Until then <see cref="TryGetEmbedded"/> returns false, and every caller must
/// treat that as "load nothing" rather than as "load without checking" — which is what
/// <see cref="CatalogLoader"/> does.
/// </remarks>
public static class CatalogPublicKey
{
    /// <summary>
    /// The placeholder the release process replaces. It is deliberately not a parseable key: a build that
    /// forgot to substitute it must fail closed, not verify against something that happens to decode.
    /// </summary>
    public const string Placeholder = "{{CATALOG_PUBKEY}}";

    /// <summary>
    /// The embedded minisign public key file contents, or <see cref="Placeholder"/> before the first
    /// release.
    /// </summary>
    public static string Embedded { get; } = Placeholder;

    /// <summary>True once a real key has been embedded.</summary>
    public static bool IsResolved => !string.Equals(Embedded, Placeholder, StringComparison.Ordinal);

    /// <summary>
    /// Parses the embedded key. Returns false while the placeholder is unresolved, or if the embedded text
    /// is not a valid minisign public key — both of which mean the same thing to a caller: there is no key
    /// to verify against, so nothing may be loaded.
    /// </summary>
    public static bool TryGetEmbedded([NotNullWhen(true)] out MinisignPublicKey? key)
    {
        key = null;

        if (!IsResolved)
        {
            return false;
        }

        try
        {
            key = MinisignSignature.ParsePublicKey(Embedded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
