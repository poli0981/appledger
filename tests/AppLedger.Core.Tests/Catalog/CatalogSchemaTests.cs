using AppLedger.Core.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Catalog;

/// <summary>
/// The shipped catalog is a fixture: every build parses it strictly and asserts the rules of
/// docs/13_CATALOG_RULES.md. These are the tests that catch a bad catalog PR before it reaches a user,
/// which is the whole point of shipping identity rules as signed data.
/// </summary>
[Trait("Category", "Catalog")]
public sealed class CatalogSchemaTests
{
    private static string SeedJson() => File.ReadAllText(TestPaths.SeedCatalog);

    [Fact]
    public void Parse_ShippedCatalog_Succeeds()
    {
        var catalog = CatalogParser.Parse(SeedJson());

        catalog.Schema.ShouldBe(CatalogParser.SupportedSchema);
        catalog.Apps.ShouldNotBeEmpty();
        catalog.HostRules.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_ShippedCatalog_EveryAppCategoryIsDeclared()
    {
        var catalog = CatalogParser.Parse(SeedJson());
        var declared = catalog.Categories.ToHashSet(StringComparer.Ordinal);

        foreach (var app in catalog.Apps)
        {
            declared.ShouldContain(app.Category, $"app '{app.Id}' uses an undeclared category");
        }
    }

    [Fact]
    public void Parse_ShippedCatalog_AppIdsAreUniqueKebabCase()
    {
        var catalog = CatalogParser.Parse(SeedJson());

        catalog.Apps.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(catalog.Apps.Count);
        foreach (var app in catalog.Apps)
        {
            app.Id.ShouldBe(app.Id.ToLowerInvariant());
            app.Id.ShouldNotContain(" ");
            app.Id.ShouldNotContain("_");
        }
    }

    [Fact]
    public void Parse_ShippedCatalog_EveryGlobIsRooted()
    {
        var catalog = CatalogParser.Parse(SeedJson());
        var expander = EnvExpander.ForValidation;

        foreach (var app in catalog.Apps)
        {
            foreach (var glob in app.Match.InstallRootGlob.Concat(app.DataDirs).Concat(app.CacheDirs))
            {
                Should.NotThrow(() => expander.ExpandToGlob(glob), $"'{glob}' in app '{app.Id}' is not rooted");
            }
        }

        foreach (var sensitive in catalog.SensitivePaths)
        {
            Should.NotThrow(() => expander.ExpandToGlob(sensitive.Glob));
        }
    }

    [Fact]
    public void Parse_ShippedCatalog_LauncherIdsExist()
    {
        var catalog = CatalogParser.Parse(SeedJson());
        var appIds = catalog.Apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var launcher in catalog.Launchers)
        {
            appIds.ShouldContain(launcher);
        }
    }

    /// <summary>
    /// conhost.exe must be an <c>attach_parent</c> rule and must not appear in <c>system</c>: the system
    /// rule runs first, so listing it there would make every console window a Windows component and break
    /// S2 fixture 4 (Windows Terminal + OpenConsole). This is a real bug the seed catalog shipped with.
    /// </summary>
    [Fact]
    public void Parse_ShippedCatalog_ConhostBelongsToAttachParentOnly()
    {
        var catalog = CatalogParser.Parse(SeedJson());

        var systemRule = catalog.HostRules.Single(r => r.Rule == HostRuleKind.System);
        systemRule.Exe.ShouldNotContain(e => string.Equals(e, "conhost.exe", StringComparison.OrdinalIgnoreCase));

        var attachParent = catalog.HostRules.Single(r => r.Rule == HostRuleKind.AttachParent);
        attachParent.Exe.ShouldContain(e => string.Equals(e, "conhost.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Host-rule fields are OR-ed, so a rule that lists both an exe and a command-line substring matches
    /// far more than its author intended. dllhost belongs in <c>dll_arg_or_system</c> by name alone.
    /// </summary>
    [Fact]
    public void Parse_ShippedCatalog_DllHostIsSelectedByNameNotByCommandLine()
    {
        var catalog = CatalogParser.Parse(SeedJson());

        var rules = catalog.HostRules.Where(r => r.Rule == HostRuleKind.DllArgOrSystem).ToList();
        rules.ShouldHaveSingleItem();
        rules[0].Exe.ShouldContain(e => string.Equals(e, "dllhost.exe", StringComparison.OrdinalIgnoreCase));
        rules[0].CmdlineContains.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_UnknownField_IsRejected()
    {
        var json = SeedJson().Replace("\"launchers\":", "\"launcherz\":", StringComparison.Ordinal);

        var error = Should.Throw<CatalogException>(() => CatalogParser.Parse(json));
        error.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_UnknownHostRuleKind_IsRejected() =>
        Should.Throw<CatalogException>(() => CatalogParser.Parse(
            SeedJson().Replace("\"rule\": \"service_group\"", "\"rule\": \"service_grup\"", StringComparison.Ordinal)));

    [Fact]
    public void Parse_NewerSchema_IsRejected() =>
        Should.Throw<CatalogException>(() => CatalogParser.Parse(
            SeedJson().Replace("\"schema\": 1", "\"schema\": 2", StringComparison.Ordinal)));

    [Fact]
    public void Parse_RemovedBuiltInCategory_IsRejected() =>
        Should.Throw<CatalogException>(() => CatalogParser.Parse(
            SeedJson().Replace("\"Browser\", ", string.Empty, StringComparison.Ordinal)));

    [Fact]
    public void Parse_MinimalCatalog_Succeeds()
    {
        // The signed fixture doubles as the smallest legal catalog.
        var minimal = File.ReadAllText(TestPaths.Minisign("sample.json"));

        var catalog = CatalogParser.Parse(minimal);

        catalog.Apps.ShouldBeEmpty();
        catalog.HostRules.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("2026.08.0", "2026.08.1", -1)]
    [InlineData("2026.08.1", "2026.08.1", 0)]
    [InlineData("2026.09.0", "2026.08.9", 1)]
    [InlineData("2027.01.0", "2026.12.9", 1)]
    public void CompareVersions_OrdersCalVer(string left, string right, int expectedSign) =>
        Math.Sign(CatalogParser.CompareVersions(left, right)).ShouldBe(expectedSign);

    [Theory]
    [InlineData("2026.8")]
    [InlineData("2026.13.0")]
    [InlineData("v2026.08.0")]
    [InlineData("2026.08.0.1")]
    public void ParseCalVer_RejectsMalformedVersions(string version) =>
        Should.Throw<CatalogException>(() => CatalogParser.ParseCalVer(version));
}
