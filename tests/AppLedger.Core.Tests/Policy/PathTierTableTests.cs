using System.Text.Json;
using AppLedger.Core.Catalog;
using AppLedger.Core.Policy;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Policy;

/// <summary>
/// The classification table of docs/11_SAFETY_POLICY.md §Path tiers, driven by <c>cases.json</c> so a
/// policy bug becomes a data row rather than a new test method (docs/19_TESTING.md §Regression rules).
/// </summary>
public sealed class PathTierTableTests
{
    private const string WindowsRoot = @"C:\Windows";
    private const string WindowsAppsRoot = @"C:\Program Files\WindowsApps";
    private const string DataRoot = @"C:\Users\fixture\AppData\Local\AppLedgerData";

    private static PathTierTable Build() => new(
        protectedOsRoots: [WindowsRoot, WindowsAppsRoot, @"C:\Program Files (x86)\WindowsApps"],
        sensitiveRoots:
        [
            @"C:\Users\fixture\AppData\Local\Microsoft\Credentials",
            @"C:\Users\fixture\AppData\Roaming\Microsoft\Protect",
            @"C:\Users\fixture\.ssh",
        ],
        sensitiveGlobs:
        [
            PathGlob.Parse(@"C:\Users\fixture\Documents\**\*.kdbx"),
            PathGlob.Parse(@"C:\Users\fixture\AppData\Local\1Password\**"),
        ],
        dataRoot: DataRoot);

    public static TheoryData<string, PathTier, PathDenyReason> Cases()
    {
        var json = File.ReadAllText(TestPaths.Fixture("Policy", "cases.json"));
        var rows = JsonSerializer.Deserialize<PolicyCase[]>(json, JsonOptions)
            ?? throw new InvalidOperationException("cases.json is empty.");

        var data = new TheoryData<string, PathTier, PathDenyReason>();
        foreach (var row in rows)
        {
            data.Add(row.Path, Enum.Parse<PathTier>(row.Tier), Enum.Parse<PathDenyReason>(row.Reason));
        }

        return data;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_MatchesCaseTable(string path, PathTier expectedTier, PathDenyReason expectedReason)
    {
        PathRules.TryNormalize(path, out var normalized, out _).ShouldBeTrue($"'{path}' should normalize");

        var tier = Build().Classify(normalized, out var reason);

        tier.ShouldBe(expectedTier);
        reason.ShouldBe(expectedReason);
    }

    /// <summary>
    /// Recycle bins and shadow-copy stores exist on every volume, so the Tier-0 rule is volume-relative
    /// rather than a list of paths on the system drive.
    /// </summary>
    [Theory]
    [InlineData(@"D:\$Recycle.Bin\S-1-5-21\file.bin")]
    [InlineData(@"E:\System Volume Information\x")]
    [InlineData(@"D:\Recovery\WindowsRE")]
    [InlineData(@"F:\Config.Msi\x.rbf")]
    public void Classify_VolumeRelativeProtectedDirectories_AreTierZeroOnEveryDrive(string path)
    {
        PathRules.TryNormalize(path, out var normalized, out _).ShouldBeTrue();

        Build().Classify(normalized, out var reason).ShouldBe(PathTier.ProtectedOs);
        reason.ShouldBe(PathDenyReason.ProtectedOs);
    }

    [Fact]
    public void Classify_DataRoot_IsNormalSoWeMayWriteThere()
    {
        var table = Build();

        table.Classify(DataRoot + @"\appledger.db", out _).ShouldBe(PathTier.Normal);
        table.IsInsideDataRoot(DataRoot + @"\logs").ShouldBeTrue();
        table.IsInsideDataRoot(@"C:\Users\fixture\Documents").ShouldBeFalse();
    }

    [Fact]
    public void CanScan_IsFalseOnlyForProtectedOs()
    {
        var table = Build();

        table.CanScan(@"C:\Windows\System32").ShouldBeFalse();
        table.CanScan(@"C:\Users\fixture\.ssh").ShouldBeTrue();
        table.CanScan(@"C:\Games\Steam").ShouldBeTrue();
    }

    /// <summary>
    /// A Tier-1 decision must never be reported with the path that triggered it: the reason code is the
    /// only thing that leaves the Agent, so the policy cannot be used as an oracle.
    /// </summary>
    [Fact]
    public void PathDecision_SensitiveAndProtected_AreNotSafeToDisplay()
    {
        var sensitive = new PathDecision(@"C:\Users\fixture\.ssh\id_ed25519", PathTier.SensitiveUserData, true, PathDenyReason.SensitiveUserData, false);
        var os = new PathDecision(@"C:\Windows\System32", PathTier.ProtectedOs, false, PathDenyReason.ProtectedOs, false);
        var normal = PathDecision.Normal(@"C:\Games\Steam");

        sensitive.SafeToDisplay.ShouldBeFalse();
        sensitive.DisplayPath.ShouldBeNull();
        os.SafeToDisplay.ShouldBeFalse();
        normal.SafeToDisplay.ShouldBeTrue();
        normal.DisplayPath.ShouldBe(@"C:\Games\Steam");
    }

    [Fact]
    public void PathDecision_Rejected_CarriesReasonAndNoPath()
    {
        var decision = PathDecision.Rejected(PathDenyReason.NetworkPath);

        decision.Allowed.ShouldBeFalse();
        decision.Canonical.ShouldBeNull();
        decision.Reason.ShouldBe(PathDenyReason.NetworkPath);
    }

    private sealed record PolicyCase(string Path, string Tier, string Reason, string? Note);
}
