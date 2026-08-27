using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Identity;

/// <summary>
/// docs/03_APP_IDENTITY.md §Install root heuristic. The install root is the fallback identity, the disk
/// footprint and the parent-adoption test at once, so one level too high merges unrelated apps and one
/// level too low splits an app into its own subfolders.
/// </summary>
public sealed class InstallRootHeuristicTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";
    private const string LocalAppData = @"C:\Users\fixture\AppData\Local";
    private const string LocalPrograms = @"C:\Users\fixture\AppData\Local\Programs";
    private const string RoamingAppData = @"C:\Users\fixture\AppData\Roaming";
    private const string UserProfile = @"C:\Users\fixture";
    private const string ProgramData = @"C:\ProgramData";
    private const string Windows = @"C:\Windows";

    private static InstallRootHeuristic Build() => new(
    [
        ProgramFiles, ProgramFilesX86, LocalPrograms, LocalAppData, RoamingAppData,
        UserProfile, ProgramData, Windows,
    ]);

    [Theory]
    [InlineData(@"C:\Program Files\Git\bin\git.exe", @"C:\Program Files\Git")]
    [InlineData(@"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", @"C:\Program Files (x86)\Steam")]
    [InlineData(@"C:\Users\fixture\AppData\Local\Programs\Microsoft VS Code\Code.exe",
        @"C:\Users\fixture\AppData\Local\Programs\Microsoft VS Code")]
    [InlineData(@"C:\ProgramData\chocolatey\lib\7zip\tools\7z.exe", @"C:\ProgramData\chocolatey")]
    public void FromImagePath_WalksUpToTheDirectoryBelowTheContainer(string image, string expected) =>
        Build().FromImagePath(image).ShouldBe(expected, StringCompareShould.IgnoreCase);

    /// <summary>
    /// Squirrel reinstalls into a new <c>app-&lt;version&gt;</c> beside the old one. Treating that as the
    /// root would give Discord a brand-new identity on every silent update, and six months of history
    /// would fragment into a dozen apps.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\fixture\AppData\Local\Discord\app-1.0.9042\Discord.exe")]
    [InlineData(@"C:\Users\fixture\AppData\Local\Discord\app-1.0.9187\Discord.exe")]
    public void FromImagePath_SquirrelVersionFolder_IsSteppedOver(string image) =>
        Build().FromImagePath(image).ShouldBe(@"C:\Users\fixture\AppData\Local\Discord", StringCompareShould.IgnoreCase);

    [Theory]
    [InlineData(@"C:\Users\fixture\AppData\Local\AppLedger\current\AppLedger.exe",
        @"C:\Users\fixture\AppData\Local\AppLedger")]
    [InlineData(@"C:\Program Files\Thing\bin\thing.exe", @"C:\Program Files\Thing")]
    [InlineData(@"C:\Program Files\Thing\bin\x64\thing.exe", @"C:\Program Files\Thing")]
    public void FromImagePath_PackagingLeaves_AreSteppedOver(string image, string expected) =>
        Build().FromImagePath(image).ShouldBe(expected, StringCompareShould.IgnoreCase);

    /// <summary>
    /// A Steam library lives on whichever drive the user picked, so <c>steamapps\common</c> is recognised
    /// by shape rather than by being in the boundary list. Without it, every game on D: would resolve to
    /// the library folder and merge into one app.
    /// </summary>
    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Deep Rock Galactic\FSD.exe",
        @"D:\SteamLibrary\steamapps\common\Deep Rock Galactic")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe",
        @"C:\Program Files (x86)\Steam\steamapps\common\Portal 2")]
    [InlineData(@"E:\Games\steamapps\common\X4\bin\X4.exe", @"E:\Games\steamapps\common\X4")]
    public void FromImagePath_SteamLibraryOnAnyDrive_StopsAtTheGameFolder(string image, string expected) =>
        Build().FromImagePath(image).ShouldBe(expected, StringCompareShould.IgnoreCase);

    /// <summary>A portable app directly under a drive root has that folder as its install root.</summary>
    [Fact]
    public void FromImagePath_PortableAppUnderADriveRoot_UsesItsOwnFolder() =>
        Build().FromImagePath(@"D:\Tools\7z\7zFM.exe").ShouldBe(@"D:\Tools", StringCompareShould.IgnoreCase);

    /// <summary>
    /// An executable sitting directly in a shared container has no install root of its own. Returning the
    /// container would make every loose exe in the user profile the same app.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\fixture\setup.exe")]
    [InlineData(@"C:\ProgramData\stub.exe")]
    [InlineData(@"C:\Program Files\loose.exe")]
    public void FromImagePath_ExecutableDirectlyInAContainer_HasNoInstallRoot(string image) =>
        Build().FromImagePath(image).ShouldBeNull();

    /// <summary>An executable at a volume root likewise belongs to no app folder.</summary>
    [Fact]
    public void FromImagePath_ExecutableAtAVolumeRoot_HasNoInstallRoot() =>
        Build().FromImagePath(@"D:\portable.exe").ShouldBeNull();

    /// <summary>
    /// The walk stops at a boundary even when the leaf looks like packaging. An app installed straight
    /// into <c>%LOCALAPPDATA%\current</c> keeps that directory rather than becoming LocalAppData.
    /// </summary>
    [Fact]
    public void FromDirectory_PackagingLeafDirectlyUnderAContainer_IsNotSteppedOver() =>
        Build().FromDirectory(@"C:\Users\fixture\AppData\Local\current")
            .ShouldBe(@"C:\Users\fixture\AppData\Local\current", StringCompareShould.IgnoreCase);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromImagePath_NothingUsable_IsNull(string? image) => Build().FromImagePath(image).ShouldBeNull();

    [Theory]
    [InlineData(@"C:\Program Files\Thing\current", true)]
    [InlineData(@"C:\Program Files\Thing\app-1.2.3", true)]
    [InlineData(@"C:\Program Files\Thing\win-x64", true)]
    [InlineData(@"C:\Program Files\Thing\app-data", false)]
    [InlineData(@"C:\Program Files\Thing\application", false)]
    [InlineData(@"C:\Program Files\Thing", false)]
    public void IsPackagingLeaf_MatchesOnlyThePackagingShapes(string directory, bool expected) =>
        InstallRootHeuristic.IsPackagingLeaf(directory).ShouldBe(expected);

    /// <summary>
    /// Deeply nested packaging must not walk the root away entirely. Four steps is more than any real
    /// layout needs, and the bound is what stops a pathological path from climbing to the drive.
    /// </summary>
    [Fact]
    public void FromImagePath_ManyStackedPackagingLeaves_StillStopsInsideTheApp() =>
        Build().FromImagePath(@"C:\Program Files\Thing\bin\x64\Release\current\thing.exe")
            .ShouldBe(@"C:\Program Files\Thing", StringCompareShould.IgnoreCase);
}
