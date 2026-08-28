using System.Globalization;
using System.Xml.Linq;
using AppLedger.App.Resources;
using Shouldly;
using Xunit;

namespace AppLedger.App.Tests;

/// <summary>
/// The resx files and the class beside them (docs/14_I18N.md, docs/19_TESTING.md §UI).
/// </summary>
/// <remarks>
/// docs/19 is explicit that a key missing from <c>vi</c> or <c>ja</c> is a <b>test failure, not a warning</b>.
/// A missing key does not throw at runtime — <c>ResourceManager</c> falls back to the neutral culture — so a
/// Vietnamese user simply sees English in one place, which nobody reports as a bug and nobody notices in
/// review either.
/// </remarks>
public sealed class LocalizationTests
{
    private static string ResourcePath(string fileName) =>
        Path.Combine(TestPaths.RepoRoot, "src", "AppLedger.App", "Resources", fileName);

    private static Dictionary<string, (string Value, string? Comment)> Read(string fileName)
    {
        var document = XDocument.Load(ResourcePath(fileName));

        return document.Root!
            .Elements("data")
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => (d.Element("value")!.Value, d.Element("comment")?.Value),
                StringComparer.Ordinal);
    }

    [Fact]
    public void Strings_EveryEnglishKeyExistsInVietnamese()
    {
        var english = Read("Strings.resx").Keys;
        var vietnamese = Read("Strings.vi.resx").Keys;

        english.Except(vietnamese, StringComparer.Ordinal).ShouldBeEmpty("keys missing from Strings.vi.resx");
    }

    [Fact]
    public void Strings_EveryEnglishKeyExistsInJapanese()
    {
        var english = Read("Strings.resx").Keys;
        var japanese = Read("Strings.ja.resx").Keys;

        english.Except(japanese, StringComparer.Ordinal).ShouldBeEmpty("keys missing from Strings.ja.resx");
    }

    /// <summary>A satellite must not carry a key the source of truth has dropped.</summary>
    [Fact]
    public void Strings_NoSatelliteCarriesAKeyEnglishHasNot()
    {
        var english = Read("Strings.resx").Keys;

        Read("Strings.vi.resx").Keys.Except(english, StringComparer.Ordinal).ShouldBeEmpty();
        Read("Strings.ja.resx").Keys.Except(english, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Fact]
    public void Strings_NoValueIsEmpty()
    {
        foreach (var file in (string[])["Strings.resx", "Strings.vi.resx", "Strings.ja.resx"])
        {
            foreach (var (key, entry) in Read(file))
            {
                entry.Value.ShouldNotBeNullOrWhiteSpace($"{file}:{key}");
            }
        }
    }

    /// <summary>
    /// docs/14 §Rules: machine-drafted Japanese is marked so a reviewer can find it, rather than being
    /// indistinguishable from text somebody actually checked.
    /// </summary>
    [Fact]
    public void Strings_JapaneseIsMarkedForReview()
    {
        foreach (var (key, entry) in Read("Strings.ja.resx"))
        {
            entry.Comment.ShouldNotBeNull($"{key} has no comment");
            entry.Comment.ShouldContain("review", Case.Insensitive, $"{key} is not marked for review");
        }
    }

    /// <summary>
    /// The generated class and the resx are two files that must agree; the generator writes both, and this
    /// is what catches a resx edited by hand afterwards.
    /// </summary>
    [Fact]
    public void Strings_ClassExposesExactlyTheKeysTheResxDefines()
    {
        var resx = Read("Strings.resx").Keys.Order(StringComparer.Ordinal);

        Strings.Keys.Order(StringComparer.Ordinal).ShouldBe(resx);
    }

    [Fact]
    public void Strings_EveryKeyResolvesToItsEnglishValue()
    {
        var english = Read("Strings.resx");

        foreach (var key in Strings.Keys)
        {
            Strings.Get(key).ShouldBe(english[key].Value, $"{key} did not resolve");
        }
    }

    /// <summary>
    /// A key with no resource behind it comes back as the key itself rather than throwing, so a typo in XAML
    /// shows up as the key on screen. That is deliberate — NFR-5 says the UI always renders — but it means a
    /// test has to be the thing that notices.
    /// </summary>
    [Fact]
    public void Get_UnknownKey_ReturnsTheKeyRatherThanThrowing() =>
        Strings.Get("No_Such_Key").ShouldBe("No_Such_Key");

    [Fact]
    public void Strings_ResolveInVietnamese()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("vi");

            var vietnamese = Read("Strings.vi.resx");
            Strings.Nav_Apps.ShouldBe(vietnamese["Nav_Apps"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Strings_ResolveInJapanese()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ja");

            var japanese = Read("Strings.ja.resx");
            Strings.Nav_Apps.ShouldBe(japanese["Nav_Apps"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// <c>{x:Static}</c> resolves through public reflection, so an internal <c>Strings</c> compiles cleanly
    /// and then throws when the window is constructed: "StaticExtension value cannot be resolved to an
    /// enumeration, static field, or static property". Only running the app finds that, which is why the
    /// visibility is asserted here.
    /// </summary>
    [Fact]
    public void Strings_IsPublicBecauseXamlResolvesItReflectively()
    {
        typeof(Strings).IsPublic.ShouldBeTrue();

        typeof(Strings)
            .GetProperty(nameof(Strings.Nav_Home), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .ShouldNotBeNull();
    }
}
