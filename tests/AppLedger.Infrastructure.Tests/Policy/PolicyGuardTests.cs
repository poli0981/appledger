using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using AppLedger.Infrastructure.Storage;
using AppLedger.Infrastructure.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Policy;

/// <summary>
/// The fixture list of docs/11_SAFETY_POLICY.md §Tests, run against the real file system. These are the
/// cases the pure <c>PathRulesTests</c> and <c>PathTierTableTests</c> in Core cannot cover, because each
/// one needs Windows to tell us what a path really is.
/// </summary>
public sealed class PolicyGuardTests : IDisposable
{
    private readonly string _scratch;
    private readonly DataRoot _dataRoot;
    private readonly PolicyGuard _guard;
    private readonly KnownFolders _folders = KnownFolders.Current;

    public PolicyGuardTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "appledger-policy-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_scratch);

        // Never the real data root: a test must not be able to touch the user's history.
        _dataRoot = new DataRoot(Path.Combine(_scratch, DataRoot.FolderName));
        _dataRoot.EnsureCreated();

        _guard = PolicyGuard.Create(catalog: null, dataRoot: _dataRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A junction we could not remove is the operating system's business, not the test's.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string System32 => _folders.System32
        ?? throw new InvalidOperationException("FOLDERID_System did not resolve.");

    [Fact]
    public void Evaluate_SystemDirectory_IsProtectedOs()
    {
        var decision = _guard.Evaluate(System32);

        decision.Tier.ShouldBe(PathTier.ProtectedOs);
        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldBe(PathDenyReason.ProtectedOs);
        decision.DisplayPath.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_ExtendedLengthPrefix_DoesNotHideTheSystemDirectory() =>
        _guard.Evaluate(PathRules.ToExtendedLength(System32) + "\\").Tier.ShouldBe(PathTier.ProtectedOs);

    /// <summary>
    /// An alternate data stream suffix is dropped before the tier decision, so <c>hosts:stream</c> cannot
    /// be used to name a Tier-0 file as if it were something else.
    /// </summary>
    [Fact]
    public void Evaluate_AlternateDataStreamSuffix_IsStrippedBeforeTiering()
    {
        var hosts = Path.Combine(System32, "drivers", "etc", "hosts");

        var decision = _guard.Evaluate(hosts + ":stream");

        decision.Tier.ShouldBe(PathTier.ProtectedOs);
        decision.Canonical.ShouldBe(hosts, StringCompareShould.IgnoreCase);
    }

    /// <summary>
    /// The case the whole canonicalization step exists for: a junction anywhere in the path must not be a
    /// way to reach System32 through a folder that looks ordinary.
    /// </summary>
    [JunctionFact]
    public void Evaluate_JunctionIntoSystem32_ResolvesToProtectedOs()
    {
        var link = Path.Combine(_scratch, "al-junc");
        Junctions.TryCreate(link, System32).ShouldBeTrue();

        var decision = _guard.Evaluate(Path.Combine(link, "drivers", "etc", "hosts"));

        decision.Unresolved.ShouldBeFalse();
        decision.Tier.ShouldBe(PathTier.ProtectedOs);
        decision.Canonical.ShouldStartWith(System32, Case.Insensitive);
    }

    /// <summary>
    /// 8.3 short names are a second spelling of the same directory. Whether a volume still generates them
    /// is a machine setting, so a machine without them skips instead of failing.
    /// </summary>
    [ShortNameFact]
    public void Evaluate_ShortNameComponents_AreExpandedBeforeTiering()
    {
        var shortWindows = Capabilities.ShortWindowsRoot!;

        var decision = _guard.Evaluate(Path.Combine(shortWindows, "SYSTEM~1"));

        decision.Tier.ShouldBe(PathTier.ProtectedOs);
        decision.Canonical.ShouldBe(System32, StringCompareShould.IgnoreCase);
    }

    /// <summary>
    /// The containment comparison uses a trailing separator, so a folder that merely starts with the same
    /// letters is a different folder. Getting this wrong would hide a whole directory tree from the user.
    /// </summary>
    [Fact]
    public void Evaluate_PrefixWithoutSeparator_IsNotProtected()
    {
        var windows = _folders.Windows ?? throw new InvalidOperationException("FOLDERID_Windows did not resolve.");

        var decision = _guard.Evaluate(windows + "Foo\\x");

        decision.Tier.ShouldBe(PathTier.WriteProtected);
        decision.Allowed.ShouldBeTrue();
    }

    /// <summary>
    /// Tier 1 counts sizes but never reports names (docs/11 §Path tiers), which is why the canonical form
    /// is kept for internal comparison while <see cref="PathDecision.DisplayPath"/> stays null.
    /// </summary>
    [Fact]
    public void Evaluate_SshPrivateKey_IsSensitiveAndNeverDisplayed()
    {
        var profile = _folders.UserProfile ?? throw new InvalidOperationException("FOLDERID_Profile did not resolve.");

        var decision = _guard.Evaluate(Path.Combine(profile, ".ssh", "id_ed25519"));

        decision.Tier.ShouldBe(PathTier.SensitiveUserData);
        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldBe(PathDenyReason.SensitiveUserData);
        decision.SafeToDisplay.ShouldBeFalse();
        decision.DisplayPath.ShouldBeNull();
    }

    /// <summary>
    /// Browser profile secrets are built-in Tier 1 rather than catalog entries, so a catalog that failed
    /// to load cannot leave saved passwords classified as ordinary files.
    /// </summary>
    [Theory]
    [InlineData("Login Data")]
    [InlineData("Cookies")]
    [InlineData("key4.db")]
    public void Evaluate_BrowserProfileSecret_IsSensitiveWithoutACatalog(string fileName)
    {
        var localAppData = _folders.LocalAppData
            ?? throw new InvalidOperationException("FOLDERID_LocalAppData did not resolve.");

        var path = Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", fileName);

        _guard.Evaluate(path).Tier.ShouldBe(PathTier.SensitiveUserData);
    }

    [Theory]
    [InlineData(@"\\server\share\x", PathDenyReason.NetworkPath)]
    [InlineData(@"\\?\UNC\server\share\x", PathDenyReason.NetworkPath)]
    [InlineData(@"\\.\PhysicalDrive0", PathDenyReason.DevicePath)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x", PathDenyReason.DevicePath)]
    [InlineData("", PathDenyReason.Empty)]
    [InlineData(@"relative\path", PathDenyReason.NotRooted)]
    public void Evaluate_UnusableShapes_AreRejectedBeforeTouchingTheFileSystem(string raw, PathDenyReason expected)
    {
        var decision = _guard.Evaluate(raw);

        decision.Canonical.ShouldBeNull();
        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldBe(expected);
    }

    /// <summary>
    /// A path whose final component does not exist cannot be opened, so reparse points cannot be
    /// collapsed. docs/11 step 3 calls for the lexical fallback plus a flag rather than a rejection.
    /// </summary>
    [Fact]
    public void Evaluate_MissingFinalComponent_FallsBackLexicallyAndFlagsUnresolved()
    {
        var missing = Path.Combine(_scratch, "does-not-exist", "either.txt");

        var decision = _guard.Evaluate(missing);

        decision.Unresolved.ShouldBeTrue();
        decision.Canonical.ShouldBe(missing, StringCompareShould.IgnoreCase);
        decision.Tier.ShouldBe(PathTier.WriteProtected);
    }

    /// <summary>
    /// An unresolvable path under a Tier-0 root stays Tier 0. This is the "for safety" half of step 3:
    /// failing to open something is never a reason to treat it as ordinary.
    /// </summary>
    [Fact]
    public void Evaluate_MissingPathUnderWindows_IsStillProtectedOs()
    {
        var decision = _guard.Evaluate(Path.Combine(System32, "no-such-file-" + Guid.NewGuid().ToString("N")));

        decision.Unresolved.ShouldBeTrue();
        decision.Tier.ShouldBe(PathTier.ProtectedOs);
    }

    [Fact]
    public void Evaluate_DataRootFile_IsTheOneWritablePlace()
    {
        var decision = _guard.Evaluate(_dataRoot.DatabasePath);

        decision.Tier.ShouldBe(PathTier.Normal);
        decision.Canonical.ShouldNotBeNull();
        _guard.IsInsideDataRoot(decision.Canonical).ShouldBeTrue();
    }

    [Fact]
    public void IsInsideDataRoot_PathOutsideTheRoot_IsFalse() =>
        _guard.IsInsideDataRoot(System32).ShouldBeFalse();

    [Fact]
    public void CanScan_ProtectedOsRoot_IsFalse() => _guard.CanScan(System32).ShouldBeFalse();

    [Fact]
    public void CanScan_OrdinaryDirectory_IsTrue() => _guard.CanScan(_scratch).ShouldBeTrue();

    [Fact]
    public void TierOfProcess_ProtectedProcess_IsZeroTouch() =>
        _guard.TierOfProcess(Path.Combine(System32, "lsass.exe"), "lsass.exe").ShouldBe(ProcessTier.ZeroTouch);

    [Fact]
    public void TierOfProcess_OrdinaryProcess_IsNormal() =>
        _guard.TierOfProcess(@"D:\Games\Title\game.exe", "game.exe").ShouldBe(ProcessTier.Normal);
}
