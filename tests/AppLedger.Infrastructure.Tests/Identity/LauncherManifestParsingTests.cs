using System.IO.Compression;
using System.Text;
using AppLedger.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Identity;

/// <summary>
/// The Epic and itch manifest parsing (docs/03_APP_IDENTITY.md step 4), against files this test writes.
/// </summary>
/// <remarks>
/// The locating and the parsing are separate for this reason: whether a machine has Epic or itch installed
/// is an accident, and the part that can be wrong on a machine that does have them is the parse. Reading a
/// real install proves the adapter talks to Windows; writing a manifest proves it reads one correctly, and
/// only the second can be asserted everywhere.
/// </remarks>
public sealed class LauncherManifestParsingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "appledger-manifest-" + Guid.NewGuid().ToString("N")[..12]);

    public LauncherManifestParsingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The id is <c>AppName</c>, never <c>DisplayName</c>. docs/03 §app_id scheme is explicit about it, and
    /// the reason is that a display name is localized and changes under the app — an id that moved when the
    /// launcher's language changed would split one app's history in two.
    /// </summary>
    [Fact]
    public void ParseEpicItem_UsesAppNameAsTheIdAndDisplayNameOnlyForDisplay()
    {
        var file = WriteText("Fortnite.item", """
            {
              "FormatVersion": 0,
              "AppName": "Fortnite",
              "DisplayName": "Fortnite — Chapitre 6",
              "InstallLocation": "D:\\Epic Games\\Fortnite"
            }
            """);

        var claim = LauncherManifestReader.ParseEpicItem(file);

        claim.ShouldNotBeNull();
        claim!.Value.Id.ShouldBe("Fortnite");
        claim.Value.Location.ShouldBe(@"D:\Epic Games\Fortnite");
        claim.Value.Name.ShouldBe("Fortnite — Chapitre 6");
    }

    /// <summary>A manifest with no install location claims nothing, so it is not an entry.</summary>
    [Fact]
    public void ParseEpicItem_WithoutAnInstallLocation_IsNull() =>
        LauncherManifestReader.ParseEpicItem(WriteText("partial.item", """
            { "AppName": "Something" }
            """)).ShouldBeNull();

    /// <summary>
    /// Someone else's file, so malformed input is an ordinary outcome rather than an exception. A launcher
    /// mid-write is the common case.
    /// </summary>
    [Fact]
    public void ParseEpicItem_OnMalformedJson_IsNull() =>
        LauncherManifestReader.ParseEpicItem(WriteText("broken.item", "{ \"AppName\": ")).ShouldBeNull();

    /// <summary>A file that is not there at all.</summary>
    [Fact]
    public void ParseEpicItem_WhenTheFileIsAbsent_IsNull() =>
        LauncherManifestReader.ParseEpicItem(Path.Combine(_root, "nothing.item")).ShouldBeNull();

    /// <summary>
    /// itch writes the receipt gzipped, and the game id is a number there. It becomes the <c>itch:</c>
    /// suffix, so it has to be formatted invariantly rather than in the current culture.
    /// </summary>
    [Fact]
    public void ParseItchReceipt_ReadsTheNumericGameId()
    {
        var directory = WriteReceipt("some-game", """
            { "game": { "id": 123456, "title": "Some Game" } }
            """);

        var claim = LauncherManifestReader.ParseItchReceipt(directory);

        claim.ShouldNotBeNull();
        claim!.Value.Id.ShouldBe("123456");
        claim.Value.Location.ShouldBe(directory);
        claim.Value.Name.ShouldBe("Some Game");
    }

    /// <summary>itch has written the id as a string in places, and both forms mean the same game.</summary>
    [Fact]
    public void ParseItchReceipt_AcceptsAStringId() =>
        LauncherManifestReader.ParseItchReceipt(WriteReceipt("string-id", """
            { "game": { "id": "778899" } }
            """))!.Value.Id.ShouldBe("778899");

    /// <summary>An app directory with no receipt is not an itch game we can name.</summary>
    [Fact]
    public void ParseItchReceipt_WithoutAReceipt_IsNull()
    {
        var directory = Path.Combine(_root, "no-receipt");
        Directory.CreateDirectory(directory);

        LauncherManifestReader.ParseItchReceipt(directory).ShouldBeNull();
    }

    /// <summary>A receipt that is not gzip at all, which is what a half-written file looks like.</summary>
    [Fact]
    public void ParseItchReceipt_OnCorruptGzip_IsNull()
    {
        var directory = Path.Combine(_root, "corrupt");
        Directory.CreateDirectory(Path.Combine(directory, ".itch"));
        File.WriteAllText(Path.Combine(directory, ".itch", "receipt.json.gz"), "not gzip at all");

        LauncherManifestReader.ParseItchReceipt(directory).ShouldBeNull();
    }

    /// <summary>Gzip that decompresses to something that is not a receipt.</summary>
    [Fact]
    public void ParseItchReceipt_WhenTheGameObjectIsMissing_IsNull() =>
        LauncherManifestReader.ParseItchReceipt(WriteReceipt("no-game", """
            { "upload": { "id": 1 } }
            """)).ShouldBeNull();

    private string WriteText(string name, string content)
    {
        var file = Path.Combine(_root, name);
        File.WriteAllText(file, content);
        return file;
    }

    private string WriteReceipt(string app, string json)
    {
        var directory = Path.Combine(_root, app);
        Directory.CreateDirectory(Path.Combine(directory, ".itch"));

        using var stream = File.Create(Path.Combine(directory, ".itch", "receipt.json.gz"));
        using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(json));

        return directory;
    }
}
