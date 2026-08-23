using AppLedger.Core.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Catalog;

/// <summary>
/// Environment expansion is an attack surface: the file being expanded is signed data that an elevated
/// Agent resolves into real directories, so only the documented allow-list may be honoured.
/// </summary>
public sealed class EnvExpanderTests
{
    [Fact]
    public void Expand_AllowedVariable_IsReplaced() =>
        EnvExpander.ForValidation.Expand(@"%PROGRAMFILES%\X").ShouldBe(@"C:\Program Files\X");

    [Fact]
    public void Expand_MultipleTokens_AreAllReplaced() =>
        EnvExpander.ForValidation.Expand(@"%USERPROFILE%\.ssh").ShouldBe(@"C:\Users\fixture\.ssh");

    [Fact]
    public void Expand_ParenthesisedVariable_IsAllowed() =>
        EnvExpander.ForValidation.Expand(@"%PROGRAMFILES(X86)%\Steam").ShouldBe(@"C:\Program Files (x86)\Steam");

    [Fact]
    public void Expand_NoTokens_IsUnchanged() =>
        EnvExpander.ForValidation.Expand(@"C:\Games\Steam").ShouldBe(@"C:\Games\Steam");

    [Theory]
    [InlineData(@"%SYSTEMROOT%\System32")]
    [InlineData(@"%PATH%\x")]
    [InlineData(@"%COMSPEC%")]
    public void Expand_VariableOutsideAllowList_Throws(string pattern) =>
        Should.Throw<FormatException>(() => EnvExpander.ForValidation.Expand(pattern))
            .Message.ShouldContain("allow-list");

    [Fact]
    public void Expand_UnterminatedToken_Throws() =>
        Should.Throw<FormatException>(() => EnvExpander.ForValidation.Expand(@"%PROGRAMFILES\X"));

    [Fact]
    public void Expand_AllowedButUnsuppliedVariable_Throws()
    {
        var sparse = new EnvExpander(new Dictionary<string, string> { ["APPDATA"] = @"C:\A" });

        Should.Throw<FormatException>(() => sparse.Expand(@"%TEMP%\x")).Message.ShouldContain("No value supplied");
    }

    /// <summary>A value outside the allow-list is dropped rather than trusted, even if a caller supplies it.</summary>
    [Fact]
    public void Constructor_IgnoresValuesOutsideTheAllowList()
    {
        var expander = new EnvExpander(new Dictionary<string, string> { ["SYSTEMROOT"] = @"C:\Windows" });

        Should.Throw<FormatException>(() => expander.Expand(@"%SYSTEMROOT%\x"));
    }

    [Fact]
    public void ExpandToGlob_ProducesARootedGlob() =>
        EnvExpander.ForValidation.ExpandToGlob(@"%LOCALAPPDATA%\Discord")
            .IsMatch(@"C:\Users\fixture\AppData\Local\Discord").ShouldBeTrue();
}

/// <summary>
/// Host-rule matching. The fields are OR-ed, which is easy to get wrong in the other direction and was
/// wrong in the shipped seed catalog (docs/24_ADR.md §Findings).
/// </summary>
public sealed class HostRuleMatcherTests
{
    private static CatalogHostRule Rule(
        HostRuleKind kind,
        IReadOnlyList<string>? exe = null,
        IReadOnlyList<string>? exeGlob = null,
        IReadOnlyList<string>? cmdline = null,
        int? pid = null,
        string? appId = null) => new()
        {
            Rule = kind,
            Exe = exe ?? [],
            ExeGlob = exeGlob ?? [],
            CmdlineContains = cmdline ?? [],
            Pid = pid,
            AppId = appId,
        };

    [Fact]
    public void Matches_ExeName_IsCaseInsensitive() =>
        HostRuleMatcher.Matches(Rule(HostRuleKind.System, exe: ["dwm.exe"]), 100, "DWM.EXE", null).ShouldBeTrue();

    [Fact]
    public void Matches_ExeGlob_Applies() =>
        HostRuleMatcher.Matches(
            Rule(HostRuleKind.AttachParent, exeGlob: ["*crashpad_handler*.exe"]),
            100, "chrome_crashpad_handler.exe", null).ShouldBeTrue();

    [Fact]
    public void Matches_CommandLineSubstring_Applies() =>
        HostRuleMatcher.Matches(
            Rule(HostRuleKind.AttachParent, cmdline: ["--type="]),
            100, "chrome.exe", "chrome.exe --type=renderer").ShouldBeTrue();

    [Fact]
    public void Matches_Pid_Applies() =>
        HostRuleMatcher.Matches(Rule(HostRuleKind.Fixed, pid: 4, appId: "sys:system"), 4, null, null).ShouldBeTrue();

    /// <summary>
    /// The OR semantics, stated as a test: a rule listing an exe and a command-line substring matches a
    /// process satisfying either one. A rule that needs both is a different rule kind.
    /// </summary>
    [Fact]
    public void Matches_FieldsAreOredNotAnded()
    {
        var rule = Rule(HostRuleKind.DllArgOrSystem, exe: ["dllhost.exe"], cmdline: ["/Processid:"]);

        HostRuleMatcher.Matches(rule, 1, "dllhost.exe", "dllhost.exe").ShouldBeTrue();
        HostRuleMatcher.Matches(rule, 1, "unrelated.exe", "unrelated.exe /Processid:{...}").ShouldBeTrue();
    }

    /// <summary>A Tier-2 process has no command line at all, and name-based rules must still work.</summary>
    [Fact]
    public void Matches_NullCommandLine_FallsBackToNameSignals()
    {
        var rule = Rule(HostRuleKind.AnticheatHelper, exe: ["EasyAntiCheat.exe"], cmdline: ["-eac"]);

        HostRuleMatcher.Matches(rule, 1, "EasyAntiCheat.exe", null).ShouldBeTrue();
        HostRuleMatcher.Matches(rule, 1, "other.exe", null).ShouldBeFalse();
    }

    [Fact]
    public void FirstMatch_ReturnsTheEarliestRule()
    {
        var rules = new[]
        {
            Rule(HostRuleKind.System, exe: ["conhost.exe"]),
            Rule(HostRuleKind.AttachParent, exe: ["conhost.exe"]),
        };

        HostRuleMatcher.FirstMatch(rules, 1, "conhost.exe", null)!.Rule.ShouldBe(HostRuleKind.System);
    }

    /// <summary>
    /// A rules file cannot change identity semantics by reordering: the evaluation order is fixed in code
    /// (docs/03 §Host rules), and the catalog's own order is only a convention.
    /// </summary>
    [Fact]
    public void InEvaluationOrder_SortsIntoTheDocumentedOrder()
    {
        var shuffled = new[]
        {
            Rule(HostRuleKind.LauncherChildren),
            Rule(HostRuleKind.AttachParent, exe: ["conhost.exe"]),
            Rule(HostRuleKind.Fixed, exe: ["explorer.exe"], appId: "sys:explorer"),
            Rule(HostRuleKind.ServiceGroup, exe: ["svchost.exe"]),
        };

        var ordered = HostRuleMatcher.InEvaluationOrder(shuffled).Select(r => r.Rule).ToArray();

        ordered.ShouldBe([
            HostRuleKind.Fixed,
            HostRuleKind.ServiceGroup,
            HostRuleKind.AttachParent,
            HostRuleKind.LauncherChildren,
        ]);
    }

    [Fact]
    public void EvaluationOrder_MatchesTheDocumentedSequence() =>
        HostRuleMatcher.EvaluationOrder.ShouldBe([
            HostRuleKind.Fixed,
            HostRuleKind.System,
            HostRuleKind.ServiceGroup,
            HostRuleKind.DllArgOrSystem,
            HostRuleKind.AttachParent,
            HostRuleKind.ScriptFromCmdline,
            HostRuleKind.AnticheatHelper,
            HostRuleKind.LauncherChildren,
        ]);
}
