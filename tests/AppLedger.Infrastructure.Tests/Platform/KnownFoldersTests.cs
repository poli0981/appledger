using AppLedger.Core.Catalog;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Platform;

/// <summary>
/// Smoke test for <c>SHGetKnownFolderPath</c> (docs/19_TESTING.md §Layers, adapter smoke). The policy is
/// only as trustworthy as these roots: a wrong or empty Windows root silently unprotects the whole OS.
/// </summary>
public sealed class KnownFoldersTests
{
    private readonly KnownFolders _folders = KnownFolders.Current;

    public static TheoryData<string> RequiredFolderNames() =>
    [
        nameof(KnownFolders.Windows),
        nameof(KnownFolders.System32),
        nameof(KnownFolders.ProgramFiles),
        nameof(KnownFolders.ProgramData),
        nameof(KnownFolders.LocalAppData),
        nameof(KnownFolders.RoamingAppData),
        nameof(KnownFolders.UserProfile),
    ];

    [Theory]
    [MemberData(nameof(RequiredFolderNames))]
    public void Resolve_RequiredFolder_IsRootedAndExists(string propertyName)
    {
        var value = (string?)typeof(KnownFolders).GetProperty(propertyName)!.GetValue(_folders);

        value.ShouldNotBeNullOrWhiteSpace();
        PathRules.TryNormalize(value, out var normalized, out _).ShouldBeTrue();
        Directory.Exists(normalized).ShouldBeTrue();
    }

    /// <summary>
    /// A folder that fails to resolve must come back null, never empty: an empty root would compare as a
    /// prefix of every path and classify the whole disk into that tier.
    /// </summary>
    [Fact]
    public void Resolve_OptionalFolders_AreNullOrUsableButNeverEmpty()
    {
        string?[] optional = [_folders.SystemX86, _folders.ProgramFilesX86, _folders.LocalAppDataLow, _folders.SavedGames, _folders.UserProgramFiles];

        foreach (var value in optional)
        {
            if (value is not null)
            {
                value.ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void Windows_MatchesTheSystemRootEnvironmentVariable() =>
        PathRules.SamePath(_folders.Windows, Environment.GetEnvironmentVariable("SystemRoot")).ShouldBeTrue();

    [Fact]
    public void System32_IsUnderTheWindowsRoot() =>
        PathRules.IsUnder(_folders.System32, _folders.Windows).ShouldBeTrue();

    [Fact]
    public void ProtectedOsRoots_ContainWindowsAndWindowsApps()
    {
        _folders.ProtectedOsRoots.ShouldContain(r => PathRules.SamePath(r, _folders.Windows));
        _folders.ProtectedOsRoots.ShouldContain(r => r.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProtectedOsRoots_AreAllRootedPaths()
    {
        foreach (var root in _folders.ProtectedOsRoots)
        {
            PathRules.TryNormalize(root, out var normalized, out _).ShouldBeTrue(root);
            normalized.ShouldBe(root, StringCompareShould.IgnoreCase);
        }
    }

    [Fact]
    public void SensitiveRoots_CoverTheCredentialStoreAndSshDirectory()
    {
        _folders.SensitiveRoots.ShouldContain(r => r.EndsWith(@"Microsoft\Credentials", StringComparison.OrdinalIgnoreCase));
        _folders.SensitiveRoots.ShouldContain(r => r.EndsWith(@"\.ssh", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every variable a catalog glob may name must have a value, or a signed rule would fail to expand and
    /// throw where the guard is built (docs/13_CATALOG_RULES.md §Glob grammar).
    /// </summary>
    [Fact]
    public void CatalogVariables_CoverEveryAllowedVariable()
    {
        var values = _folders.CatalogVariables;

        foreach (var name in EnvExpander.AllowedVariables)
        {
            values.ShouldContainKey(name);
            values[name].ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void CatalogVariables_HaveNoTrailingSeparator() =>
        _folders.CatalogVariables.Values.ShouldAllBe(v => !v.EndsWith('\\'));
}
