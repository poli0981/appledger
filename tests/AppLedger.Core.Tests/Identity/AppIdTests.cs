using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Identity;

/// <summary>
/// The `app_id` scheme of docs/03_APP_IDENTITY.md. These strings are the primary key of every metrics
/// table, so their formatting is a storage contract: a change to how an id is built orphans an app's
/// history rather than renaming it.
/// </summary>
public sealed class AppIdTests
{
    [Fact]
    public void Msix_UsesThePackageFamilyName() =>
        AppId.Msix("Microsoft.WindowsTerminal_8wekyb3d8bbwe").Value
            .ShouldBe("msix:Microsoft.WindowsTerminal_8wekyb3d8bbwe");

    [Theory]
    [InlineData("1091500", "steam:1091500")]
    public void Steam_UsesTheAppId(string input, string expected) => AppId.Steam(input).Value.ShouldBe(expected);

    [Fact]
    public void Catalog_PrefixesWithCat() => AppId.Catalog("discord").Value.ShouldBe("cat:discord");

    /// <summary>
    /// Uninstall keys are messy: braces, mixed case and double spaces are cosmetic and must not fork an
    /// app's history into two rows.
    /// </summary>
    [Theory]
    [InlineData("Discord", "uninst:discord")]
    [InlineData("{Discord}", "uninst:discord")]
    [InlineData("My  App", "uninst:my-app")]
    [InlineData("My_App", "uninst:my-app")]
    [InlineData("My-App", "uninst:my-app")]
    [InlineData("  Spaced  ", "uninst:spaced")]
    [InlineData("{}", "uninst:unnamed")]
    public void Uninstall_NormalizesTheKeyName(string key, string expected) =>
        AppId.Uninstall(key).Value.ShouldBe(expected);

    [Fact]
    public void Winget_KeepsCaseBecauseWingetIdsAreCaseSensitive() =>
        AppId.Winget("Microsoft.PowerToys").Value.ShouldBe("winget:Microsoft.PowerToys");

    [Fact]
    public void Script_HashesTheLowerCasedPath()
    {
        var id = AppId.Script(@"C:\Work\scraper.py");

        id.Prefix.ShouldBe("script");
        id.Suffix.Length.ShouldBe(AppId.HashLength);
        id.ShouldBe(AppId.Script(@"c:\work\SCRAPER.PY"));
    }

    [Fact]
    public void Root_HashesTheInstallRootAndDiffersPerPath()
    {
        var a = AppId.Root(@"D:\Tools\7z");
        var b = AppId.Root(@"D:\Tools\other");

        a.ShouldNotBe(b);
        a.Prefix.ShouldBe("root");
        a.Suffix.Length.ShouldBe(AppId.HashLength);
    }

    [Fact]
    public void SystemIds_AreTheDocumentedConstants()
    {
        AppId.Windows.Value.ShouldBe("sys:windows");
        AppId.Explorer.Value.ShouldBe("sys:explorer");
        AppId.SystemProcess.Value.ShouldBe("sys:system");
        AppId.Idle.Value.ShouldBe("sys:idle");
        AppId.ServiceGroup.Value.ShouldBe("sys:services");
        AppId.Service("Dnscache").Value.ShouldBe("sys:service:Dnscache");
    }

    [Fact]
    public void IsSystem_IsTrueForTheSysFamilyOnly()
    {
        AppId.Windows.IsSystem.ShouldBeTrue();
        AppId.Service("Dnscache").IsSystem.ShouldBeTrue();
        AppId.Catalog("discord").IsSystem.ShouldBeFalse();
    }

    [Theory]
    [InlineData("cat:discord", AppSource.Catalog)]
    [InlineData("msix:Microsoft.WindowsTerminal_8wekyb3d8bbwe", AppSource.Msix)]
    [InlineData("sys:service:Dnscache", AppSource.System)]
    [InlineData("root:2a7b1c2d3e4f5061", AppSource.Root)]
    [InlineData("user:0123456789abcdef0123456789abcdef", AppSource.User)]
    public void Parse_RoundTripsAndReportsTheSource(string value, AppSource expected)
    {
        var id = AppId.Parse(value);

        id.Value.ShouldBe(value);
        id.Source.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("discord")]
    [InlineData(":discord")]
    [InlineData("cat:")]
    [InlineData("bogus:discord")]
    public void TryParse_RejectsUnknownShapes(string? value) => AppId.TryParse(value, out _).ShouldBeFalse();

    [Fact]
    public void Parse_UnknownPrefix_Throws() => Should.Throw<FormatException>(() => AppId.Parse("bogus:x"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Factories_RejectEmptyComponents(string value) =>
        Should.Throw<ArgumentException>(() => AppId.Catalog(value));

    [Fact]
    public void User_UsesACompactGuid() =>
        AppId.User(Guid.Empty).Value.ShouldBe("user:00000000000000000000000000000000");
}

/// <summary>The precedence and confidence tables, which decide which of several matches wins.</summary>
public sealed class AppSourceAndConfidenceTests
{
    /// <summary>
    /// The precedence order of docs/03: user override, then system, then catalog, then store and launcher
    /// identities, then the registry, then package managers, then script, then the root fallback.
    /// </summary>
    [Fact]
    public void Wins_FollowsTheDocumentedPrecedence()
    {
        AppSource.User.Wins(AppSource.Catalog).ShouldBeTrue();
        AppSource.Catalog.Wins(AppSource.Msix).ShouldBeTrue();
        AppSource.Msix.Wins(AppSource.Steam).ShouldBeTrue();
        AppSource.Steam.Wins(AppSource.Uninstall).ShouldBeTrue();
        AppSource.Uninstall.Wins(AppSource.Scoop).ShouldBeTrue();
        AppSource.Script.Wins(AppSource.Root).ShouldBeTrue();

        AppSource.Root.Wins(AppSource.Script).ShouldBeFalse();
        AppSource.Catalog.Wins(AppSource.Catalog).ShouldBeFalse();
    }

    [Fact]
    public void ToPrefix_And_FromPrefix_RoundTripEverySource()
    {
        foreach (var source in Enum.GetValues<AppSource>())
        {
            AppSourceExtensions.FromPrefix(source.ToPrefix()).ShouldBe(source);
        }
    }

    [Fact]
    public void FromPrefix_UnknownPrefix_IsNull() => AppSourceExtensions.FromPrefix("nope").ShouldBeNull();

    [Fact]
    public void ForSource_MatchesTheConfidenceTable()
    {
        Confidence.ForSource(AppSource.Msix).ShouldBe(1.00);
        Confidence.ForSource(AppSource.Catalog).ShouldBe(0.95);
        Confidence.ForSource(AppSource.Uninstall).ShouldBe(0.90);
        Confidence.ForSource(AppSource.Script).ShouldBe(0.85);
        Confidence.ForSource(AppSource.Root).ShouldBe(0.30);
    }

    [Fact]
    public void Adopted_DiscountsTheParentsConfidence() =>
        Confidence.Adopted(1.00).ShouldBe(0.9, 0.0001);

    /// <summary>
    /// The threshold is strict: S2 fixture 7 expects portable 7-Zip at exactly 0.60, and that case must
    /// not raise a "?" badge.
    /// </summary>
    [Theory]
    [InlineData(0.95, false)]
    [InlineData(0.60, false)]
    [InlineData(0.59, true)]
    [InlineData(0.30, true)]
    public void ShouldPromptUser_TriggersBelowSixty(double confidence, bool expected) =>
        Confidence.ShouldPromptUser(confidence).ShouldBe(expected);
}

/// <summary>Process identity is always the pair, never the PID, because Windows reuses PIDs aggressively.</summary>
public sealed class ProcessKeyTests
{
    [Fact]
    public void CouldBeParentOf_RequiresTheParentToExistFirst()
    {
        var parent = new ProcessKey(100, 1_000);
        var child = new ProcessKey(200, 2_000);

        parent.CouldBeParentOf(child).ShouldBeTrue();
        child.CouldBeParentOf(parent).ShouldBeFalse();
    }

    /// <summary>
    /// The PID-reuse guard: a new process that inherited an exited parent's PID has a later create time,
    /// so it can never be adopted by the instance that used to own that PID.
    /// </summary>
    [Fact]
    public void CouldBeParentOf_SamePidReused_IsFalse()
    {
        var recycled = new ProcessKey(100, 5_000);
        var child = new ProcessKey(200, 4_000);

        recycled.CouldBeParentOf(child).ShouldBeFalse();
    }

    [Fact]
    public void Equality_ConsidersBothPidAndCreateTime()
    {
        new ProcessKey(100, 1_000).ShouldBe(new ProcessKey(100, 1_000));
        new ProcessKey(100, 1_000).ShouldNotBe(new ProcessKey(100, 1_001));
    }

    [Fact]
    public void CompareTo_OrdersByCreateTimeThenPid()
    {
        var keys = new[] { new ProcessKey(300, 2_000), new ProcessKey(100, 1_000), new ProcessKey(50, 2_000) };

        Array.Sort(keys);

        keys.ShouldBe([new ProcessKey(100, 1_000), new ProcessKey(50, 2_000), new ProcessKey(300, 2_000)]);
        (new ProcessKey(100, 1_000) < new ProcessKey(50, 2_000)).ShouldBeTrue();
        (new ProcessKey(50, 2_000) >= new ProcessKey(100, 1_000)).ShouldBeTrue();
    }

    [Fact]
    public void ToString_IsPidAtCreateTime() => new ProcessKey(4, 0).ToString().ShouldBe("4@0");
}
