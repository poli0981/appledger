using AppLedger.Core.Policy;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Policy;

/// <summary>
/// docs/11_SAFETY_POLICY.md §Process access tiers. Every case here decides whether we open a handle to
/// someone else's process at all, so a false <see cref="ProcessTier.Normal"/> is the most expensive
/// mistake in the codebase: it is the one that could get a user banned from a game.
/// </summary>
public sealed class ProcessTierTableTests
{
    private static ProcessTierTable Build() => new();

    [Theory]
    [InlineData("lsass.exe")]
    [InlineData("LSASS.EXE")]
    [InlineData("csrss.exe")]
    [InlineData("wininit.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("services.exe")]
    [InlineData("smss.exe")]
    [InlineData("MsMpEng.exe")]
    [InlineData("NisSrv.exe")]
    [InlineData("SecurityHealthService.exe")]
    [InlineData("MsSense.exe")]
    public void Classify_ProtectedProcessName_IsZeroTouch(string imageFileName) =>
        Build().Classify(null, imageFileName).ShouldBe(ProcessTier.ZeroTouch);

    [Theory]
    [InlineData("start_protected_game.exe")]
    [InlineData("EasyAntiCheat.exe")]
    [InlineData("EasyAntiCheat_EOS_Setup.exe")]
    [InlineData("BEService.exe")]
    public void Classify_AntiCheatHelperName_IsZeroTouch(string imageFileName) =>
        Build().Classify(@"D:\Games\Some Game\" + imageFileName, imageFileName).ShouldBe(ProcessTier.ZeroTouch);

    [Theory]
    [InlineData(@"D:\Steam\steamapps\common\Game\EasyAntiCheat\game.exe")]
    [InlineData(@"D:\Steam\steamapps\common\Game\BattlEye\beclient.exe")]
    [InlineData(@"C:\Games\Title\GameGuard\gg.exe")]
    [InlineData(@"d:\steam\steamapps\common\game\easyanticheat\game.exe")]
    public void Classify_PathUnderAntiCheatDirectory_IsZeroTouch(string imagePath) =>
        Build().Classify(imagePath, "game.exe").ShouldBe(ProcessTier.ZeroTouch);

    [Theory]
    [InlineData(@"C:\Program Files\Discord\Discord.exe", "Discord.exe")]
    [InlineData(@"C:\Windows\explorer.exe", "explorer.exe")]
    [InlineData(@"D:\Games\BattlEyeFanClub\app.exe", "app.exe")]
    [InlineData(@"D:\Games\Title\EasyAntiCheatNotReally\app.exe", "app.exe")]
    public void Classify_OrdinaryProcess_IsNormal(string imagePath, string imageFileName) =>
        Build().Classify(imagePath, imageFileName).ShouldBe(ProcessTier.Normal);

    /// <summary>
    /// A directory name is only a signal when it is a whole path component. Substring matching would
    /// promote every folder that merely starts with the same letters, and a Tier-2 promotion is
    /// irreversible for the lifetime of the process — we would silently stop enriching a normal app.
    /// </summary>
    [Fact]
    public void Classify_DirectoryNameIsMatchedWholeNotAsSubstring() =>
        Build().Classify(@"D:\BattlEyeBackups\tool.exe", "tool.exe").ShouldBe(ProcessTier.Normal);

    /// <summary>
    /// The final component is the executable, not a directory, so a folder-name rule must not fire on it.
    /// The executable names that do matter are in the helper list and are matched by name.
    /// </summary>
    [Fact]
    public void Classify_TrailingComponentIsNotTreatedAsDirectory() =>
        Build().Classify(@"D:\Games\Title\GameGuard", "GameGuard").ShouldBe(ProcessTier.Normal);

    [Fact]
    public void Classify_CatalogExtension_AddsProtectedProcess()
    {
        var table = new ProcessTierTable(additionalProtectedProcesses: ["vgtray.exe"]);

        table.Classify(@"C:\Program Files\Riot Vanguard\vgtray.exe", "vgtray.exe").ShouldBe(ProcessTier.ZeroTouch);
    }

    [Fact]
    public void Classify_CatalogExtension_AddsAntiCheatDirectory()
    {
        var table = new ProcessTierTable(additionalAntiCheatDirectories: ["Denuvo Anti-Cheat"]);

        table.Classify(@"D:\Games\Title\Denuvo Anti-Cheat\x.exe", "x.exe").ShouldBe(ProcessTier.ZeroTouch);
    }

    /// <summary>
    /// The image file name is always available (docs/03_APP_IDENTITY.md §Resolution pipeline), but the
    /// path is null until enrichment has run — and for a Tier-2 process it stays null forever. Deciding
    /// from the name alone therefore has to work.
    /// </summary>
    [Fact]
    public void Classify_NameAloneDecides_WhenPathIsUnknown() =>
        Build().Classify(null, "lsass.exe").ShouldBe(ProcessTier.ZeroTouch);

    [Fact]
    public void Classify_PathAloneDecides_WhenNameIsMissing() =>
        Build().Classify(@"C:\Windows\System32\lsass.exe", null).ShouldBe(ProcessTier.ZeroTouch);

    [Fact]
    public void Classify_NothingKnown_IsNormal() =>
        Build().Classify(null, null).ShouldBe(ProcessTier.Normal);
}
