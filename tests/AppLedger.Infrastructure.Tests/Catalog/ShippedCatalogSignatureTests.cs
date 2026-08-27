using AppLedger.Core.Catalog;
using AppLedger.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Catalog;

/// <summary>
/// Guards the one thing that can rot silently now that a real key is embedded: a
/// <c>catalog/appledger-catalog.json.minisig</c> that no longer matches the catalog beside it.
/// </summary>
/// <remarks>
/// The failure mode is quiet and expensive. Edit the catalog, forget to re-sign, and every Agent falls back
/// to the built-in policy minimum with no rules at all — while the schema test, which does not verify
/// signatures, stays green. This turns that into a failed build instead.
/// </remarks>
public sealed class ShippedCatalogSignatureTests
{
    /// <summary>The detached signature beside the shipped catalog.</summary>
    internal static string SignaturePath => TestPaths.SeedCatalog + ".minisig";

    [SignedCatalogFact]
    public void ShippedCatalogSignature_MatchesTheCatalogBesideIt()
    {
        CatalogPublicKey.TryGetEmbedded(out var key).ShouldBeTrue();
        var verifier = new MinisignVerifier(key!);

        verifier.Verify(File.ReadAllBytes(TestPaths.SeedCatalog), File.ReadAllText(SignaturePath))
            .ShouldBe(CatalogVerifyResult.Valid, "re-sign the catalog after editing it");
    }

    [SignedCatalogFact]
    public void ShippedCatalog_LoadsThroughTheRealLoader()
    {
        var loader = CatalogLoader.TryCreateFromEmbeddedKey(
            new EnvExpander(AppLedger.Infrastructure.Platform.KnownFolders.Current.CatalogVariables),
            NullLogger<CatalogLoader>.Instance);

        loader.ShouldNotBeNull();

        var result = loader!.Load(TestPaths.SeedCatalog, SignaturePath);

        result.Succeeded.ShouldBeTrue(result.Error);
        result.Document!.Schema.ShouldBe(CatalogParser.SupportedSchema);
    }
}

/// <summary>
/// A fact that runs only once the shipped catalog has been signed. Signing needs the secret key, which
/// lives only in the <c>CATALOG_MINISIGN_KEY</c> GitHub secret and on the maintainer's machine, so an
/// unsigned working tree is a legitimate state — not a failure.
/// </summary>
internal sealed class SignedCatalogFactAttribute : FactAttribute
{
    public SignedCatalogFactAttribute()
    {
        if (!File.Exists(ShippedCatalogSignatureTests.SignaturePath))
        {
            Skip = "catalog/appledger-catalog.json.minisig is not present; sign the catalog to enable this check.";
        }
    }
}
