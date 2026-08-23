using AppLedger.Core.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Catalog;

/// <summary>
/// Parsing of the minisign file formats, against the shared corpus in <c>tests/fixtures/minisign/</c>.
/// Verification itself is an Infrastructure smoke test (NSec); what is asserted here is that the parser
/// reads the right bytes out of the right lines and refuses anything it does not understand.
/// </summary>
[Trait("Category", "Catalog")]
public sealed class MinisignParserTests
{
    private const string GoodKeyId = "05E0E1316342AA8C";
    private const string WrongKeyId = "D35927E1F7DC5C7A";

    private static string Read(string name) => File.ReadAllText(TestPaths.Minisign(name));

    [Fact]
    public void ParsePublicKey_ReadsKeyIdAndKey()
    {
        var key = MinisignSignature.ParsePublicKey(Read("test.pub"));

        key.KeyId.Count.ShouldBe(8);
        key.PublicKey.Count.ShouldBe(32);
        key.KeyIdHex.ShouldBe(GoodKeyId);
        key.UntrustedComment.ShouldContain("minisign public key");
    }

    [Fact]
    public void Parse_PrehashedSignature_ReadsEveryField()
    {
        var signature = MinisignSignature.Parse(Read("sample.json.minisig"));

        signature.Algorithm.ShouldBe(MinisignAlgorithm.Prehashed);
        signature.KeyId.Count.ShouldBe(8);
        signature.Signature.Count.ShouldBe(64);
        signature.GlobalSignature.Count.ShouldBe(64);
        signature.KeyIdHex.ShouldBe(GoodKeyId);
        signature.TrustedComment.ShouldContain("file:sample.json");
    }

    /// <summary>
    /// docs/13 requires prehashed <c>ED</c>. The legacy form parses — it is a valid minisign file — but the
    /// algorithm is surfaced so the loader can refuse it rather than silently accepting a weaker mode.
    /// </summary>
    [Fact]
    public void Parse_LegacySignature_IsReadableButReportsLegacy() =>
        MinisignSignature.Parse(Read("sample.json.legacy.minisig")).Algorithm.ShouldBe(MinisignAlgorithm.Legacy);

    /// <summary>A key-id mismatch is refused before any crypto runs, so a wrong key costs nothing.</summary>
    [Fact]
    public void MatchesKey_WrongKey_IsRejectedWithoutCrypto()
    {
        var trusted = MinisignSignature.ParsePublicKey(Read("test.pub"));
        var fromOtherKey = MinisignSignature.Parse(Read("sample.json.wrongkey.minisig"));

        fromOtherKey.KeyIdHex.ShouldBe(WrongKeyId);
        fromOtherKey.MatchesKey(trusted).ShouldBeFalse();

        var ownKey = MinisignSignature.ParsePublicKey(Read("test-wrong.pub"));
        fromOtherKey.MatchesKey(ownKey).ShouldBeTrue();
    }

    /// <summary>
    /// A corrupted signature still has the right shape: only verification can reject it, which is why the
    /// parser must not be the place that decides validity.
    /// </summary>
    [Fact]
    public void Parse_CorruptSignature_StillParses()
    {
        var corrupt = MinisignSignature.Parse(Read("sample.json.corrupt.minisig"));
        var good = MinisignSignature.Parse(Read("sample.json.minisig"));

        corrupt.Signature.Count.ShouldBe(64);
        corrupt.Signature.ShouldNotBe(good.Signature);
    }

    [Fact]
    public void GlobalSignedData_IsSignatureFollowedByTrustedComment()
    {
        var signature = MinisignSignature.Parse(Read("sample.json.minisig"));

        var data = signature.GlobalSignedData();

        data.Length.ShouldBe(64 + System.Text.Encoding.UTF8.GetByteCount(signature.TrustedComment));
        data[..64].ShouldBe([.. signature.Signature]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("untrusted comment: only one line")]
    [InlineData("wrong prefix: x\nAAAA\ntrusted comment: y\nAAAA")]
    [InlineData("untrusted comment: x\nnot-base64!!\ntrusted comment: y\nAAAA")]
    public void Parse_Malformed_Throws(string content) =>
        Should.Throw<FormatException>(() => MinisignSignature.Parse(content));

    [Fact]
    public void Parse_WrongBlobLength_Throws()
    {
        var content = "untrusted comment: x\n" + Convert.ToBase64String(new byte[10]) + "\ntrusted comment: y\n"
                      + Convert.ToBase64String(new byte[64]);

        Should.Throw<FormatException>(() => MinisignSignature.Parse(content))
            .Message.ShouldContain("74 bytes");
    }

    [Fact]
    public void TryParse_Malformed_ReportsErrorWithoutThrowing()
    {
        MinisignSignature.TryParse("garbage", out var signature, out var error).ShouldBeFalse();

        signature.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ParsePublicKey_WrongAlgorithmTag_Throws()
    {
        var blob = new byte[42];
        blob[0] = (byte)'X';
        blob[1] = (byte)'x';
        var content = "untrusted comment: x\n" + Convert.ToBase64String(blob);

        Should.Throw<FormatException>(() => MinisignSignature.ParsePublicKey(content));
    }

    [Fact]
    public void FormatKeyId_ReversesBytesLikeMinisign() =>
        MinisignSignature.FormatKeyId([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08])
            .ShouldBe("0807060504030201");
}
