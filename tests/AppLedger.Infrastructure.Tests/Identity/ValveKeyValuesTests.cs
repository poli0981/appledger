using AppLedger.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Identity;

/// <summary>
/// The KeyValues reading behind the Steam index (docs/03_APP_IDENTITY.md step 4).
/// </summary>
/// <remarks>
/// Worth testing closely because every failure mode is quiet. A path whose escaped separators survive as
/// <c>\\</c> never matches a canonical image path, so the library is simply absent; an object name read as a
/// scalar adds a junk entry that claims a directory; and a missed <c>installdir</c> turns a game into a
/// <c>root:</c> hash. None of them throws.
/// </remarks>
public sealed class ValveKeyValuesTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"      "C:\\Program Files (x86)\\Steam"
                "label"     ""
                "apps"
                {
                    "228980"    "123456"
                }
            }
            "1"
            {
                "path"      "D:\\SteamLibrary"
            }
        }
        """;

    private const string AppManifest = """
        "AppState"
        {
            "appid"     "1091500"
            "name"      "ProtectedGame"
            "installdir"    "ProtectedGame"
            "UserConfig"
            {
                "language"  "english"
            }
        }
        """;

    /// <summary>
    /// Every library path, with escaped separators collapsed. If they survived as <c>\\</c> the directory
    /// would never match anything.
    /// </summary>
    [Fact]
    public void Values_ReadsEveryLibraryPathAndUnescapesIt()
    {
        ValveKeyValues.Values(LibraryFolders, "path").ShouldBe(
        [
            @"C:\Program Files (x86)\Steam",
            @"D:\SteamLibrary",
        ]);
    }

    /// <summary>An object's name is not one of its values, however it is indented.</summary>
    [Fact]
    public void Scalars_TreatsAnObjectNameAsAKeyRatherThanAValue()
    {
        var keys = ValveKeyValues.Scalars(LibraryFolders).Select(p => p.Key).ToList();

        keys.ShouldNotContain("libraryfolders");
        keys.ShouldNotContain("apps");
        keys.ShouldContain("path");
        keys.ShouldContain("label");

        // The one inside `apps` is a real scalar: an appid keyed by its own number.
        keys.ShouldContain("228980");
    }

    /// <summary>The two values the Steam index actually needs out of an app manifest.</summary>
    [Fact]
    public void Value_ReadsTheAppIdAndInstallDirectory()
    {
        ValveKeyValues.Value(AppManifest, "appid").ShouldBe("1091500");
        ValveKeyValues.Value(AppManifest, "installdir").ShouldBe("ProtectedGame");
        ValveKeyValues.Value(AppManifest, "name").ShouldBe("ProtectedGame");
    }

    /// <summary>
    /// Keys are matched lower-cased, because the files are not consistent about case across Steam versions
    /// and a case-sensitive read would lose a library on some installs and not others.
    /// </summary>
    [Fact]
    public void Value_MatchesRegardlessOfTheCaseTheFileUsed()
    {
        ValveKeyValues.Value("""
            "AppState" { "AppID" "7" "InstallDir" "Game" }
            """, "appid").ShouldBe("7");
    }

    /// <summary>An empty value is a value, not a missing key.</summary>
    [Fact]
    public void Value_ReadsAnEmptyStringAsItself() =>
        ValveKeyValues.Value(LibraryFolders, "label").ShouldBe(string.Empty);

    /// <summary>Line comments are skipped; KeyValues has no block comment form.</summary>
    [Fact]
    public void Scalars_IgnoresLineComments() =>
        ValveKeyValues.Value("""
            // "path" "C:\\Decoy"
            "libraryfolders" { "path" "D:\\Real" }
            """, "path").ShouldBe(@"D:\Real");

    /// <summary>A truncated file yields what it has rather than throwing: it is someone else's file.</summary>
    [Fact]
    public void Scalars_OnTruncatedInput_DoesNotThrow() =>
        Should.NotThrow(() => ValveKeyValues.Scalars("""
            "AppState" { "appid" "109
            """).ToList());

    /// <summary>A key with no value at all is not reported as one.</summary>
    [Fact]
    public void Value_WhenTheKeyIsAbsent_IsNull() =>
        ValveKeyValues.Value(AppManifest, "installlocation").ShouldBeNull();
}
