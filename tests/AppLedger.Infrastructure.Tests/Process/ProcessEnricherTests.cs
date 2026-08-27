using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Process;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Process;

/// <summary>
/// Adapter smoke test for the enrichment path, and the executable form of the zero-touch guarantee
/// (docs/11_SAFETY_POLICY.md §Tests).
/// </summary>
public sealed class ProcessEnricherTests
{
    /// <summary>The counting mock docs/11 §Tests asks for.</summary>
    private sealed class CountingCounter : IProcessCounter
    {
        internal List<int> Opened { get; } = [];

        public void OnOpenProcess(int pid) => Opened.Add(pid);
    }

    private static ProcessKey Self
    {
        get
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            return new ProcessKey(current.Id, current.StartTime.ToFileTimeUtc());
        }
    }

    /// <summary>
    /// The single most important assertion in the repository. A Tier-2 process must produce **zero** handle
    /// opens — not a reduced-rights one, not one that fails and is retried. Counting is the only way to
    /// prove absence; inferring it from null fields would also pass if we opened a handle and threw the
    /// answers away.
    /// </summary>
    [Fact]
    public void Enrich_ZeroTouchProcess_OpensNoHandleAtAll()
    {
        var counter = new CountingCounter();
        var enricher = new ProcessEnricher(counter);

        var result = enricher.Enrich(Self, ProcessTier.ZeroTouch);

        counter.Opened.ShouldBeEmpty();
        result.Attempted.ShouldBeFalse();
        result.ImagePath.ShouldBeNull();
        result.CommandLine.ShouldBeNull();
        result.UserSid.ShouldBeNull();
        result.PackageFamilyName.ShouldBeNull();
        result.Integrity.ShouldBe(IntegrityLevel.Unknown);
        result.Elevated.ShouldBeNull();
        result.Architecture.ShouldBeNull();
    }

    [Fact]
    public void Enrich_NormalProcess_OpensExactlyOneHandle()
    {
        var counter = new CountingCounter();
        var enricher = new ProcessEnricher(counter);

        enricher.Enrich(Self, ProcessTier.Normal);

        counter.Opened.ShouldHaveSingleItem();
        counter.Opened[0].ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void Enrich_OwnProcess_ReadsTheFactsThatNeedAHandle()
    {
        var result = new ProcessEnricher().Enrich(Self, ProcessTier.Normal);

        result.Attempted.ShouldBeTrue();
        result.ImagePath.ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);
        result.CommandLine.ShouldNotBeNullOrWhiteSpace();
        result.UserSid.ShouldStartWith("S-1-");
        result.Integrity.ShouldBeOneOf(IntegrityLevel.Medium, IntegrityLevel.High, IntegrityLevel.System);
        result.Elevated.ShouldNotBeNull();
        result.Architecture.ShouldBeOneOf("x64", "x86", "ARM64", "ARM");
    }

    /// <summary>
    /// A test host is not MSIX-packaged, and "not packaged" must come back as null rather than as an
    /// error: <c>APPMODEL_ERROR_NO_PACKAGE</c> is how Windows says so.
    /// </summary>
    [Fact]
    public void Enrich_UnpackagedProcess_ReportsNoPackageFamilyName() =>
        new ProcessEnricher().Enrich(Self, ProcessTier.Normal).PackageFamilyName.ShouldBeNull();

    /// <summary>
    /// The PID-reuse guard. Windows hands PIDs out again, so a handle opened by PID alone may belong to a
    /// different process than the snapshot named. Enriching it would attach a stranger's command line to
    /// an app, which is exactly the class of bug docs/03_APP_IDENTITY.md keys everything on
    /// <c>(pid, createTime)</c> to prevent.
    /// </summary>
    [Fact]
    public void Enrich_CreateTimeMismatch_RefusesRatherThanEnrichingTheWrongProcess()
    {
        var wrongInstance = Self with { CreateTime = Self.CreateTime + 1 };

        var result = new ProcessEnricher().Enrich(wrongInstance, ProcessTier.Normal);

        result.Attempted.ShouldBeFalse();
        result.ImagePath.ShouldBeNull();
    }

    [Fact]
    public void Enrich_ProcessThatDoesNotExist_IsUnavailableRatherThanThrowing()
    {
        // An odd PID above the plausible range: Windows allocates PIDs in multiples of four.
        var ghost = new ProcessKey(0x7FFFFFFD, 1);

        var result = new ProcessEnricher().Enrich(ghost, ProcessTier.Normal);

        result.Attempted.ShouldBeFalse();
    }

    /// <summary>
    /// The system process is PPL, so opening it either fails outright or yields a handle that refuses
    /// every query. Either way the call must come back empty-handed instead of throwing.
    /// </summary>
    [Fact]
    public void Enrich_SystemProcess_DegradesQuietly()
    {
        var system = new NtProcessSource().Snapshot().ToArray().Single(s => s.Key.Pid == 4);

        var result = Should.NotThrow(() => new ProcessEnricher().Enrich(system.Key, ProcessTier.Normal));

        result.CommandLine.ShouldBeNull();
    }
}
