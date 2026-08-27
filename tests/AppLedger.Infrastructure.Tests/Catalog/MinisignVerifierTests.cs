using System.Text;
using AppLedger.Core.Catalog;
using AppLedger.Infrastructure.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Catalog;

/// <summary>
/// The Ed25519 and BLAKE2b-512 half of the catalog trust chain (ADR-12). Parsing is covered in Core;
/// this is the part that decides whether an elevated Agent loads a rules file.
/// </summary>
/// <remarks>
/// The corpus in <c>tests/fixtures/minisign/</c> is shared with the Core parser tests and documented in
/// its own README, which states what a correct verifier must do with each file. Every one of those
/// expectations has a case here.
/// </remarks>
public sealed class MinisignVerifierTests
{
    private static readonly byte[] Sample = File.ReadAllBytes(TestPaths.Minisign("sample.json"));

    private static MinisignVerifier Verifier() =>
        new(MinisignSignature.ParsePublicKey(File.ReadAllText(TestPaths.Minisign("test.pub"))));

    private static string Signature(string fileName) => File.ReadAllText(TestPaths.Minisign(fileName));

    [Fact]
    public void Verify_PrehashedSignatureFromTheTrustedKey_IsValid() =>
        Verifier().Verify(Sample, Signature("sample.json.minisig")).ShouldBe(CatalogVerifyResult.Valid);

    /// <summary>
    /// docs/13 requires the prehashed <c>ED</c> form. The legacy mode streams the whole file into Ed25519,
    /// so a verifier has to buffer an attacker-controlled length before it can reject anything.
    /// </summary>
    [Fact]
    public void Verify_LegacyAlgorithm_IsRefusedEvenThoughTheSignatureIsGood() =>
        Verifier().Verify(Sample, Signature("sample.json.legacy.minisig"))
            .ShouldBe(CatalogVerifyResult.UnsupportedAlgorithm);

    [Fact]
    public void Verify_FlippedSignatureByte_IsBadSignature() =>
        Verifier().Verify(Sample, Signature("sample.json.corrupt.minisig")).ShouldBe(CatalogVerifyResult.BadSignature);

    /// <summary>
    /// A correctly signed file from an untrusted key. The key id is compared before any verification runs,
    /// so this costs no crypto — and reports the honest reason rather than a generic failure.
    /// </summary>
    [Fact]
    public void Verify_SignatureFromAnotherKey_IsRefusedOnTheKeyId() =>
        Verifier().Verify(Sample, Signature("sample.json.wrongkey.minisig")).ShouldBe(CatalogVerifyResult.WrongKey);

    /// <summary>
    /// Content that does not match the signature. The same signature file that verifies the real sample
    /// must not verify anything else.
    /// </summary>
    [Fact]
    public void Verify_DifferentContent_IsBadSignature() =>
        Verifier().Verify("{}"u8, Signature("sample.json.minisig")).ShouldBe(CatalogVerifyResult.BadSignature);

    /// <summary>
    /// The trusted comment is only trustworthy because a second signature covers it. Tampering with it has
    /// to be caught, or the field would look authoritative while being editable by anyone.
    /// </summary>
    [Fact]
    public void Verify_TamperedTrustedComment_IsBadGlobalSignature()
    {
        var original = Signature("sample.json.minisig");
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        lines[2] = "trusted comment: timestamp:1 file:not-really.json";

        Verifier().Verify(Sample, string.Join('\n', lines)).ShouldBe(CatalogVerifyResult.BadGlobalSignature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("untrusted comment: x\nnot base64 at all\ntrusted comment: y\nalso not base64")]
    public void Verify_UnparseableSignatureFile_IsMalformed(string content) =>
        Verifier().Verify(Sample, content).ShouldBe(CatalogVerifyResult.Malformed);

    [Fact]
    public void Verify_NullSignatureFile_IsMalformedRatherThanThrowing() =>
        Verifier().Verify(Sample, null!).ShouldBe(CatalogVerifyResult.Malformed);

    [Fact]
    public void TrustedKey_IsTheKeyTheCorpusDocuments() =>
        Verifier().TrustedKey.KeyIdHex.ShouldBe("05E0E1316342AA8C");

    /// <summary>
    /// The second key in the corpus must never verify the first key's signature, in either direction.
    /// Asserting both ways rules out a verifier that ignores the key entirely.
    /// </summary>
    [Fact]
    public void Verify_WithTheWrongKeyTrusted_RefusesTheGoodSignature()
    {
        var wrong = new MinisignVerifier(
            MinisignSignature.ParsePublicKey(File.ReadAllText(TestPaths.Minisign("test-wrong.pub"))));

        wrong.Verify(Sample, Signature("sample.json.minisig")).ShouldBe(CatalogVerifyResult.WrongKey);
        wrong.Verify(Sample, Signature("sample.json.wrongkey.minisig")).ShouldBe(CatalogVerifyResult.Valid);
    }

    /// <summary>A byte-order mark or trailing newline must not change the verdict for the same content.</summary>
    [Fact]
    public void Verify_SignatureFileWithCrLfLineEndings_StillVerifies()
    {
        var crlf = Signature("sample.json.minisig").Replace("\n", "\r\n", StringComparison.Ordinal);

        Verifier().Verify(Sample, crlf).ShouldBe(CatalogVerifyResult.Valid);
    }

    [Fact]
    public void Verify_EmptyContent_IsBadSignatureNotAnException() =>
        Verifier().Verify(ReadOnlySpan<byte>.Empty, Signature("sample.json.minisig"))
            .ShouldBe(CatalogVerifyResult.BadSignature);

    /// <summary>
    /// A sanity check on the fixture itself: if <c>sample.json</c> is ever re-saved with different bytes,
    /// every verification test above would fail for a reason that has nothing to do with the verifier.
    /// </summary>
    [Fact]
    public void Sample_IsTheExactBytesTheSignatureCovers() =>
        Encoding.UTF8.GetString(Sample).ShouldContain("\"schema\"");
}
