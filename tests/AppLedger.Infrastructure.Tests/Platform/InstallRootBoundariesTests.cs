using AppLedger.Infrastructure.Platform;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Platform;

/// <summary>
/// The list that decides where an install root stops (docs/03_APP_IDENTITY.md §Install-root heuristic).
/// </summary>
/// <remarks>
/// Worth its own tests because every failure here is silent. A boundary list missing the Windows root does
/// not throw — it walks every system binary up to <c>C:\Windows</c> and reports one enormous app. Two hosts
/// with different lists do not disagree either: they simply write history under different app identities,
/// and the split only shows up later as an app that appears to have restarted its life.
/// </remarks>
public sealed class InstallRootBoundariesTests
{
    private readonly IReadOnlyList<string> _boundaries = InstallRootBoundaries.For(KnownFolders.Current);

    [Fact]
    public void For_IncludesEveryProtectedOsRoot()
    {
        // The whole reason the list exists. docs/11_SAFETY_POLICY.md Tier 0 roots are never scanned as apps,
        // and the heuristic must stop at them rather than resolve into them.
        foreach (var root in KnownFolders.Current.ProtectedOsRoots)
        {
            _boundaries.ShouldContain(root, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void For_IncludesTheUserAndMachineInstallRoots()
    {
        var folders = KnownFolders.Current;

        _boundaries.ShouldContain(folders.ProgramFiles!, StringComparer.OrdinalIgnoreCase);
        _boundaries.ShouldContain(folders.ProgramData!, StringComparer.OrdinalIgnoreCase);
        _boundaries.ShouldContain(folders.LocalAppData!, StringComparer.OrdinalIgnoreCase);
        _boundaries.ShouldContain(folders.UserProfile!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void For_HasNoDuplicates()
    {
        // ProgramFiles and ProgramFilesX86 are the same directory on ARM64 hosts under some SKUs, and
        // ProtectedOsRoots overlaps the folder properties by design. A duplicate is harmless to the
        // heuristic and a nuisance to read, so the list is deduplicated case-insensitively.
        _boundaries.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(_boundaries.Count);
    }

    [Fact]
    public void For_ContainsNoEmptyEntries()
    {
        // KnownFolders returns null for folders this SKU does not have. An empty string in the list would
        // match every path prefix and stop the walk immediately, giving every process its own app.
        _boundaries.ShouldAllBe(b => !string.IsNullOrWhiteSpace(b));
    }

    [Fact]
    public void For_IsRootedEverywhere()
    {
        _boundaries.ShouldAllBe(b => Path.IsPathRooted(b));
    }
}
