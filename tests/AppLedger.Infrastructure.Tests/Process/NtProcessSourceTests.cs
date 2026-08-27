using System.Diagnostics;
using AppLedger.Infrastructure.Process;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Infrastructure.Tests.Process;

/// <summary>
/// Adapter smoke test for <c>NtQuerySystemInformation(SystemProcessInformation)</c>
/// (docs/19_TESTING.md §Layers). The offsets are asserted separately; what this proves is that the call
/// itself works on this OS and architecture and that the parse walks the entry list correctly.
/// </summary>
public sealed class NtProcessSourceTests
{
    private readonly ITestOutputHelper _output;

    public NtProcessSourceTests(ITestOutputHelper output) => _output = output;

    private static long CurrentCreateTime
    {
        get
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            return current.StartTime.ToFileTimeUtc();
        }
    }

    [Fact]
    public void Snapshot_FindsTheTestProcessItself()
    {
        var source = new NtProcessSource();

        var samples = source.Snapshot().ToArray();
        var self = samples.SingleOrDefault(s => s.Key.Pid == Environment.ProcessId);

        self.Key.Pid.ShouldBe(Environment.ProcessId);
        self.Key.CreateTime.ShouldBe(CurrentCreateTime);
        self.ImageName.ShouldBe(Path.GetFileName(Environment.ProcessPath), StringCompareShould.IgnoreCase);
        self.ThreadCount.ShouldBeGreaterThan(0);
        self.HandleCount.ShouldBeGreaterThan(0);
        self.WorkingSetPrivate.ShouldBeGreaterThan(0);
        self.SessionId.ShouldBe(source.CurrentSessionId);
    }

    /// <summary>
    /// PID 4 is the System process and PID 0 the idle process. Both are always present, and the idle
    /// process is the one entry whose image-name buffer is null — the case that would throw if the parse
    /// dereferenced it blindly.
    /// </summary>
    [Fact]
    public void Snapshot_IncludesTheIdleAndSystemProcessesWithoutChokingOnTheEmptyImageName()
    {
        var samples = new NtProcessSource().Snapshot().ToArray();

        samples.ShouldContain(s => s.Key.Pid == 0);
        samples.ShouldContain(s => s.Key.Pid == 4);
        samples.ShouldAllBe(s => s.ImageName != null);
    }

    [Fact]
    public void Snapshot_WithASessionFilter_ReturnsOnlyThatSession()
    {
        var source = new NtProcessSource();

        var filtered = source.Snapshot(source.CurrentSessionId).ToArray();

        filtered.ShouldNotBeEmpty();
        filtered.ShouldAllBe(s => s.SessionId == source.CurrentSessionId);
    }

    [Fact]
    public void Snapshot_WithoutAFilter_SeesAtLeastAsManyProcessesAsWithOne()
    {
        var source = new NtProcessSource();

        var all = source.Snapshot().Length;
        var mine = source.Snapshot(source.CurrentSessionId).Length;

        all.ShouldBeGreaterThanOrEqualTo(mine);
    }

    /// <summary>
    /// The returned span points into a buffer the source reuses, so a second call must overwrite it rather
    /// than hand back the previous poll's numbers. Cumulative CPU time can only go up, which is the
    /// cheapest observable proof that the second call really re-read the system.
    /// </summary>
    /// <remarks>
    /// CPU time is quantised to the scheduler tick (~15.6 ms), so the spin has to run until the counter
    /// actually moves rather than for a fixed amount of work — in a Release build the fixed version burned
    /// less than one tick and the assertion failed for a reason that had nothing to do with the adapter.
    /// </remarks>
    [Fact]
    public void Snapshot_CalledTwice_ReturnsFreshCumulativeCounters()
    {
        var source = new NtProcessSource();

        var first = source.Snapshot().ToArray().Single(s => s.Key.Pid == Environment.ProcessId);
        var deadline = Stopwatch.StartNew();
        var second = first;

        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Spin();
            second = source.Snapshot().ToArray().Single(s => s.Key.Pid == Environment.ProcessId);
            if (second.UserTime + second.KernelTime > first.UserTime + first.KernelTime)
            {
                break;
            }
        }

        second.Key.CreateTime.ShouldBe(first.Key.CreateTime);
        (second.UserTime + second.KernelTime).ShouldBeGreaterThan(first.UserTime + first.KernelTime);
    }

    [Fact]
    public void Snapshot_DoesNotHitTheBufferCeilingOnAnOrdinaryMachine()
    {
        var source = new NtProcessSource();

        source.Snapshot();

        source.BufferCeilingReached.ShouldBeFalse();
    }

    /// <summary>
    /// The budget note docs/05_COLLECTOR.md §Budget requires for a collector-path change. This is a
    /// measurement, not a gate: it prints the per-poll cost rather than asserting a threshold, because a
    /// shared CI runner is the wrong place to fail a build over microseconds.
    /// </summary>
    [Fact]
    public void Snapshot_PollCost_IsReportedForTheBudgetNote()
    {
        var source = new NtProcessSource();
        source.Snapshot();

        const int Polls = 60;
        var stopwatch = Stopwatch.StartNew();
        var processes = 0;
        for (var i = 0; i < Polls; i++)
        {
            processes = source.Snapshot().Length;
        }

        stopwatch.Stop();

        var perPollMs = stopwatch.Elapsed.TotalMilliseconds / Polls;
        _output.WriteLine($"{Polls} polls over {processes} processes: {perPollMs:F2} ms per poll.");

        perPollMs.ShouldBeGreaterThan(0);
    }

    private static void Spin()
    {
        // Busy work rather than a sleep: we need CPU time to accrue, not wall-clock time to pass.
        var sink = 0d;
        for (var i = 1; i < 3_000_000; i++)
        {
            sink += Math.Sqrt(i);
        }

        GC.KeepAlive(sink);
    }
}
