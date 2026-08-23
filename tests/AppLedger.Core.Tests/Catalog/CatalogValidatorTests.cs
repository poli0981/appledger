using AppLedger.Core.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Catalog;

/// <summary>
/// The semantic rules, exercised against hand-built documents rather than the shipped catalog, so each
/// rule has a test that fails for exactly one reason.
/// </summary>
[Trait("Category", "Catalog")]
public sealed class CatalogValidatorTests
{
    private static CatalogDocument Build(
        IReadOnlyList<CatalogApp>? apps = null,
        IReadOnlyList<CatalogHostRule>? hostRules = null,
        IReadOnlyList<CatalogAntiCheat>? antiCheat = null,
        IReadOnlyList<string>? launchers = null) => new()
        {
            Schema = 1,
            Version = "2026.08.0",
            GeneratedUtc = DateTimeOffset.UnixEpoch,
            MinAppVersion = "0.1.0",
            Categories = CatalogValidator.BuiltInCategories,
            Apps = apps ?? [],
            HostRules = hostRules ?? [],
            AntiCheat = antiCheat ?? [],
            Launchers = launchers ?? [],
        };

    private static CatalogApp App(string id, CatalogMatch match, string category = "Utility") => new()
    {
        Id = id,
        Name = id,
        Category = category,
        Match = match,
    };

    private static void Validate(CatalogDocument doc) => CatalogValidator.Validate(doc, EnvExpander.ForValidation);

    [Fact]
    public void Validate_SignerOnlyMatch_IsAccepted() =>
        Should.NotThrow(() => Validate(Build([App("x", new CatalogMatch { Signer = ["Contoso Ltd."] })])));

    [Fact]
    public void Validate_PackageFamilyOnlyMatch_IsAccepted() =>
        Should.NotThrow(() => Validate(Build([App("x", new CatalogMatch { PackageFamily = ["Contoso.App_8wek"] })])));

    [Fact]
    public void Validate_ExeAndRootMatch_IsAccepted() =>
        Should.NotThrow(() => Validate(Build([
            App("x", new CatalogMatch { Exe = ["x.exe"], InstallRootGlob = [@"%PROGRAMFILES%\X"] })])));

    /// <summary>
    /// An exe-only rule would claim any file with that name anywhere — the case that makes portable
    /// 7-Zip resolve to <c>cat:7zip</c> instead of falling through to <c>root:</c> (S2 fixture 7).
    /// </summary>
    [Fact]
    public void Validate_ExeOnlyMatch_IsRejected()
    {
        var error = Should.Throw<CatalogException>(() =>
            Validate(Build([App("x", new CatalogMatch { Exe = ["x.exe"] })])));

        error.Message.ShouldContain("install_root_glob");
    }

    [Fact]
    public void Validate_EmptyMatch_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build([App("x", new CatalogMatch())])));

    [Fact]
    public void Validate_ExeWithPath_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build([
            App("x", new CatalogMatch { Exe = [@"bin\x.exe"], InstallRootGlob = [@"%PROGRAMFILES%\X"] })])));

    [Fact]
    public void Validate_UnrootedGlob_IsRejected()
    {
        var error = Should.Throw<CatalogException>(() => Validate(Build([
            App("x", new CatalogMatch { Exe = ["x.exe"], InstallRootGlob = [@"*\Steam"] })])));

        error.Message.ShouldContain("not rooted");
    }

    [Fact]
    public void Validate_DriveWildcardGlob_IsAccepted() =>
        Should.NotThrow(() => Validate(Build([
            App("x", new CatalogMatch { Exe = ["x.exe"], InstallRootGlob = [@"?:\Steam"] })])));

    [Fact]
    public void Validate_GlobWithDisallowedVariable_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build([
            App("x", new CatalogMatch { Exe = ["x.exe"], InstallRootGlob = [@"%SYSTEMROOT%\X"] })])));

    [Fact]
    public void Validate_DuplicateAppId_IsRejected()
    {
        var match = new CatalogMatch { Signer = ["Contoso Ltd."] };

        Should.Throw<CatalogException>(() => Validate(Build([App("x", match), App("x", match)])));
    }

    [Theory]
    [InlineData("With Space")]
    [InlineData("UPPER")]
    [InlineData("under_score")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public void Validate_NonKebabAppId_IsRejected(string id) =>
        Should.Throw<CatalogException>(() => Validate(Build([App(id, new CatalogMatch { Signer = ["C"] })])));

    [Fact]
    public void Validate_FixedRuleWithoutAppId_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(
            hostRules: [new CatalogHostRule { Rule = HostRuleKind.Fixed, Exe = ["explorer.exe"] }])));

    [Fact]
    public void Validate_FixedRuleAssigningNonSystemId_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(
            hostRules: [new CatalogHostRule { Rule = HostRuleKind.Fixed, Exe = ["x.exe"], AppId = "cat:x" }])));

    [Fact]
    public void Validate_NonFixedRuleWithAppId_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(
            hostRules: [new CatalogHostRule { Rule = HostRuleKind.System, Exe = ["x.exe"], AppId = "sys:windows" }])));

    [Fact]
    public void Validate_RuleWithoutSelector_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(
            hostRules: [new CatalogHostRule { Rule = HostRuleKind.System }])));

    /// <summary>launcher_children selects by the parent's category, so it needs no selector of its own.</summary>
    [Fact]
    public void Validate_LauncherChildrenWithoutSelector_IsAccepted() =>
        Should.NotThrow(() => Validate(Build(
            hostRules: [new CatalogHostRule { Rule = HostRuleKind.LauncherChildren }])));

    [Fact]
    public void Validate_AntiCheatClaimingDriverWithoutOne_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(antiCheat: [new CatalogAntiCheat
        {
            Id = "eac",
            Name = "Easy Anti-Cheat",
            Dirs = ["EasyAntiCheat"],
            MatchConfidence = AntiCheatMatchConfidence.Driver,
        }])));

    [Fact]
    public void Validate_AntiCheatWithNoSignalAndNoneConfidence_IsAccepted() =>
        Should.NotThrow(() => Validate(Build(antiCheat: [new CatalogAntiCheat
        {
            Id = "vac",
            Name = "Valve Anti-Cheat",
            MatchConfidence = AntiCheatMatchConfidence.None,
        }])));

    [Fact]
    public void Validate_LauncherNamingUnknownApp_IsRejected() =>
        Should.Throw<CatalogException>(() => Validate(Build(launchers: ["nope"])));

    /// <summary>
    /// An app rule and an anticheat entry both mint `cat:&lt;id&gt;`, so the same id in both lists would be
    /// two different apps sharing one primary key.
    /// </summary>
    [Fact]
    public void Validate_IdUsedByBothAnAppAndAnAntiCheatEntry_IsRejected()
    {
        var doc = Build(
            apps: [App("eac", new CatalogMatch { Signer = ["Epic Games, Inc."] })],
            antiCheat: [new CatalogAntiCheat
            {
                Id = "eac",
                Name = "Easy Anti-Cheat",
                Drivers = ["EasyAntiCheat.sys"],
                MatchConfidence = AntiCheatMatchConfidence.Driver,
            }]);

        Should.Throw<CatalogException>(() => Validate(doc)).Message.ShouldContain("cat:eac");
    }

    [Fact]
    public void Validate_DistinctAppAndAntiCheatIds_AreAccepted() =>
        Should.NotThrow(() => Validate(Build(
            apps: [App("epic", new CatalogMatch { Signer = ["Epic Games, Inc."] })],
            antiCheat: [new CatalogAntiCheat
            {
                Id = "eac",
                Name = "Easy Anti-Cheat",
                Drivers = ["EasyAntiCheat.sys"],
                MatchConfidence = AntiCheatMatchConfidence.Driver,
            }])));
}
