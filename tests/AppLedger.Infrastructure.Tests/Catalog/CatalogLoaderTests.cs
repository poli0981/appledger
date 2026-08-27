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
    /// The one that matters most. <c>{{CATALOG_PUBKEY}}</c> is unresolved until the first release, and a
    /// build with no trusted key must load nothing rather than load without checking. If this ever returns
    /// a loader, an elevated Agent will happily read unsigned rules off a user-writable disk.
    /// </summary>
    [Fact]
    public void TryCreateFromEmbeddedKey_WithThePlaceholderStillInPlace_FailsClosed()
    {
        CatalogPublicKey.IsResolved.ShouldBeFalse(
            "when the release key lands, this test documents that the placeholder is gone");

        CatalogLoader.TryCreateFromEmbeddedKey(EnvExpander.ForValidation, NullLogger<CatalogLoader>.Instance)
            .ShouldBeNull();
    }

    [Fact]
    public void TryGetEmbedded_WithThePlaceholderStillInPlace_ReturnsNoKey()
    {
        CatalogPublicKey.TryGetEmbedded(out var key).ShouldBeFalse();

        key.ShouldBeNull();
    }
}
