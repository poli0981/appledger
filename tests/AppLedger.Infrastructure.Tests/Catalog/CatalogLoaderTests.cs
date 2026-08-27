using AppLedger.Core.Catalog;
using AppLedger.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Catalog;

/// <summary>
/// The loader's job is to refuse things. Every test here is about a way in, and what it means that the
/// way is shut (docs/13_CATALOG_RULES.md §Update flow, docs/01_ARCHITECTURE.md §Degraded modes).
/// </summary>
public sealed class CatalogLoaderTests : IDisposable
{
    private readonly string _scratch;
    private readonly CatalogLoader _loader;

    public CatalogLoaderTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "appledger-catalog-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_scratch);

        var key = MinisignSignature.ParsePublicKey(File.ReadAllText(TestPaths.Minisign("test.pub")));
        _loader = new CatalogLoader(
            new MinisignVerifier(key),
            EnvExpander.ForValidation,
            NullLogger<CatalogLoader>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Copies a fixture pair into the scratch directory and returns the two paths.</summary>
    private (string Catalog, string Signature) Stage(string signatureFixture = "sample.json.minisig")
    {
        var catalog = Path.Combine(_scratch, "appledger-catalog.json");
        var signature = catalog + ".minisig";

        File.Copy(TestPaths.Minisign("sample.json"), catalog, overwrite: true);
        File.Copy(TestPaths.Minisign(signatureFixture), signature, overwrite: true);

        return (catalog, signature);
    }

    [Fact]
    public void Load_VerifiedCatalog_IsParsedStrictly()
    {
        var (catalog, signature) = Stage();

        var result = _loader.Load(catalog, signature);

        result.Succeeded.ShouldBeTrue();
        result.Outcome.ShouldBe(CatalogLoadOutcome.Loaded);
        result.VerifyResult.ShouldBe(CatalogVerifyResult.Valid);
        result.Document!.Schema.ShouldBe(CatalogParser.SupportedSchema);
    }

    [Theory]
    [InlineData("sample.json.corrupt.minisig", CatalogVerifyResult.BadSignature)]
    [InlineData("sample.json.wrongkey.minisig", CatalogVerifyResult.WrongKey)]
    [InlineData("sample.json.legacy.minisig", CatalogVerifyResult.UnsupportedAlgorithm)]
    public void Load_BadSignature_LoadsNothingAndSaysWhy(string fixture, CatalogVerifyResult expected)
    {
        var (catalog, signature) = Stage(fixture);

        var result = _loader.Load(catalog, signature);

        result.Succeeded.ShouldBeFalse();
        result.Outcome.ShouldBe(CatalogLoadOutcome.SignatureRejected);
        result.VerifyResult.ShouldBe(expected);
        result.Document.ShouldBeNull();
    }

    /// <summary>
    /// A good signature over a file that is no longer the file: the content is edited after signing, so the
    /// verdict must be about the signature rather than about the JSON.
    /// </summary>
    [Fact]
    public void Load_ContentEditedAfterSigning_IsRejectedBeforeParsing()
    {
        var (catalog, signature) = Stage();
        File.AppendAllText(catalog, "\n");

        var result = _loader.Load(catalog, signature);

        result.Outcome.ShouldBe(CatalogLoadOutcome.SignatureRejected);
        result.VerifyResult.ShouldBe(CatalogVerifyResult.BadSignature);
    }

    [Fact]
    public void Load_MissingFiles_LoadNothing()
    {
        var missing = Path.Combine(_scratch, "not-here.json");

        _loader.Load(missing, missing + ".minisig").Outcome.ShouldBe(CatalogLoadOutcome.Missing);
    }

    /// <summary>
    /// The 4 MB cap of docs/11 §Privilege boundary is checked from the file length, before a byte is read,
    /// so an oversized file is never brought into the elevated Agent's memory at all.
    /// </summary>
    [Fact]
    public void Load_OversizedCatalog_IsRejectedWithoutBeingRead()
    {
        var (catalog, signature) = Stage();
        using (var stream = new FileStream(catalog, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(CatalogLoader.MaxCatalogBytes + 1);
        }

        var result = _loader.Load(catalog, signature);

        result.Outcome.ShouldBe(CatalogLoadOutcome.TooLarge);
        result.VerifyResult.ShouldBeNull();
    }

    /// <summary>A catalog is never downgraded, even when the older file is correctly signed.</summary>
    [Fact]
    public void Load_NotNewerThanTheActiveCatalog_IsRefused()
    {
        var (catalog, signature) = Stage();
        var active = _loader.Load(catalog, signature).Document;
        active.ShouldNotBeNull();

        var again = _loader.Load(catalog, signature, active);

        again.Outcome.ShouldBe(CatalogLoadOutcome.NotNewer);
        again.Document.ShouldBeNull();
    }

    /// <summary>
    /// The release key is embedded, so a build can be built into a working verifier. The key id is
    /// asserted rather than just "some key parsed": a build that shipped the *test* key would otherwise
    /// look perfectly healthy while trusting a keypair whose secret half is in this repository.
    /// </summary>
    [Fact]
    public void TryGetEmbedded_ReturnsTheReleaseKeyAndNotATestKey()
    {
        CatalogPublicKey.IsResolved.ShouldBeTrue();
        CatalogPublicKey.TryGetEmbedded(out var key).ShouldBeTrue();

        key!.KeyIdHex.ShouldBe("6ED9A5D305231FDB");
        key.KeyIdHex.ShouldNotBe("05E0E1316342AA8C", "that is the test corpus key");
        key.KeyIdHex.ShouldNotBe("D35927E1F7DC5C7A", "that is the wrong-key fixture");
    }

    [Fact]
    public void TryCreateFromEmbeddedKey_WithAKeyEmbedded_ProducesAWorkingLoader() =>
        CatalogLoader.TryCreateFromEmbeddedKey(EnvExpander.ForValidation, NullLogger<CatalogLoader>.Instance)
            .ShouldNotBeNull();

    /// <summary>
    /// The fail-closed branches, which the embedded key now bypasses. They still have to work: a build that
    /// forgot the substitution, or shipped a mangled key, must load nothing rather than load without
    /// checking. An elevated Agent reading unsigned rules off a user-writable disk is the whole failure
    /// this refuses.
    /// </summary>
    [Theory]
    [InlineData(CatalogPublicKey.Placeholder)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("untrusted comment: mangled\nnot base64")]
    [InlineData("untrusted comment: too short\nRWTbHyMF06XZbg==")]
    public void TryParse_UnusableEmbeddedKey_FailsClosed(string? embedded)
    {
        CatalogPublicKey.TryParse(embedded, out var key).ShouldBeFalse();

        key.ShouldBeNull();
    }

    [Fact]
    public void TryParse_NullEmbeddedKey_FailsClosed() =>
        CatalogPublicKey.TryParse(null, out _).ShouldBeFalse();
}
