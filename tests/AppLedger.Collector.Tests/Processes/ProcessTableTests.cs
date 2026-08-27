using AppLedger.Collector.Processes;
using AppLedger.Core.Identity;
using AppLedger.Core.Process;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Processes;

/// <summary>
/// The delta arithmetic and instance lifecycle of docs/01_ARCHITECTURE.md §Collector pipeline. Every case
/// here is one where a wrong answer would still look like a number a user might believe.
/// </summary>
public sealed class ProcessTableTests
{
    private const long Ms = 10_000;                 // 100 ns ticks per millisecond
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static RawProcessSample Sample(
        int pid,
        long createTime = 1,
        string imageName = "app.exe",
        long userTime = 0,
        long kernelTime = 0,
        long readBytes = 0,
        long writeBytes = 0,
        long hardFaults = 0,
        long wsPrivate = 1024,
        int threads = 4,
        int handles = 40,
        int parentPid = 0,
        int sessionId = 1) => new()
        {
            Key = new ProcessKey(pid, createTime),
            ImageName = imageName,
            ParentPid = parentPid,
            SessionId = sessionId,
            UserTime = userTime,
            KernelTime = kernelTime,
            ReadTransferCount = readBytes,
            WriteTransferCount = writeBytes,
            HardFaultCount = hardFaults,
            WorkingSetPrivate = wsPrivate,
            WorkingSet = wsPrivate * 2,
            PagefileUsage = wsPrivate * 3,
            ThreadCount = threads,
            HandleCount = handles,
        };

    /// <summary>Runs a poll, advancing both clocks by the same amount.</summary>
    private static ProcessTick Poll(ProcessTable table, RawProcessSample[] snapshot, int second) =>
        table.Update(snapshot, 1_700_000_000 + second, TimeSpan.FromSeconds(second));

    /// <summary>
    /// The first poll has no interval behind it, so it cannot produce a delta. Reporting one would mean
    /// charting a process's entire lifetime of I/O as if it happened in that second.
    /// </summary>
    [Fact]
    public void Update_FirstPoll_ReportsInstancesStartedAndNoDeltas()
    {
        var table = new ProcessTable(logicalCpuCount: 4);

        var tick = Poll(table, [Sample(100), Sample(200)], 0);

        tick.Rebaselined.ShouldBeTrue();
        tick.Deltas.ShouldBeEmpty();
        tick.Started.Select(s => s.Key.Pid).ShouldBe([100, 200]);
        tick.Exited.ShouldBeEmpty();
        table.LiveCount.ShouldBe(2);
    }

    [Fact]
    public void Update_SecondPoll_TurnsCumulativeCountersIntoDeltas()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, readBytes: 1_000, writeBytes: 500, hardFaults: 7)], 0);

        var tick = Poll(table, [Sample(100, readBytes: 3_500, writeBytes: 900, hardFaults: 10)], 1);

        tick.Rebaselined.ShouldBeFalse();
        var delta = tick.Deltas.ShouldHaveSingleItem();
        delta.IoRead.ShouldBe(2_500);
        delta.IoWrite.ShouldBe(400);
        delta.HardFaults.ShouldBe(3);
    }

    /// <summary>Gauges are the value at the instant, never a difference.</summary>
    [Fact]
    public void Update_Gauges_AreReportedAsValuesNotDifferences()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, wsPrivate: 1_000, threads: 4, handles: 40)], 0);

        var delta = Poll(table, [Sample(100, wsPrivate: 4_000, threads: 9, handles: 70)], 1)
            .Deltas.ShouldHaveSingleItem();

        delta.WsPrivate.ShouldBe(4_000);
        delta.Ws.ShouldBe(8_000);
        delta.CommitBytes.ShouldBe(12_000);
        delta.Threads.ShouldBe(9);
        delta.Handles.ShouldBe(70);
    }

    /// <summary>
    /// Task Manager's convention: 100 % means every core busy. A process that burned one core for a full
    /// second on a four-core box is at 25 %, not 100 %.
    /// </summary>
    [Theory]
    [InlineData(4, 1000, 25.0)]
    [InlineData(4, 2000, 50.0)]
    [InlineData(4, 4000, 100.0)]
    [InlineData(8, 1000, 12.5)]
    [InlineData(1, 500, 50.0)]
    public void Update_CpuPercent_IsDividedByLogicalCpuCount(int cpus, long busyMs, double expected)
    {
        var table = new ProcessTable(cpus);
        Poll(table, [Sample(100)], 0);

        var delta = Poll(table, [Sample(100, userTime: busyMs * Ms)], 1).Deltas.ShouldHaveSingleItem();

        delta.CpuPct.ShouldBe(expected, 0.01);
    }

    /// <summary>
    /// The scheduler can credit a process with more CPU than the interval had, and a 340 % reading on a
    /// chart reads as a bug rather than as a busy moment.
    /// </summary>
    [Fact]
    public void Update_CpuPercent_IsCappedAtOneHundred()
    {
        var table = new ProcessTable(logicalCpuCount: 2);
        Poll(table, [Sample(100)], 0);

        var delta = Poll(table, [Sample(100, userTime: 9_000 * Ms)], 1).Deltas.ShouldHaveSingleItem();

        delta.CpuPct.ShouldBe(100d);
    }

    [Fact]
    public void Update_CpuMilliseconds_SplitUserAndKernel()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100)], 0);

        var delta = Poll(table, [Sample(100, userTime: 120 * Ms, kernelTime: 30 * Ms)], 1)
            .Deltas.ShouldHaveSingleItem();

        delta.CpuUserMs.ShouldBe(120);
        delta.CpuKernelMs.ShouldBe(30);
    }

    /// <summary>
    /// A counter that goes backwards is a reading we cannot trust. Zero is the only honest answer: a
    /// negative would draw a spike downward, and the raw value would draw one the size of the process's
    /// whole lifetime.
    /// </summary>
    [Fact]
    public void Update_CounterThatWentBackwards_ContributesZeroRatherThanANonsenseValue()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, readBytes: 5_000, userTime: 900 * Ms)], 0);

        var delta = Poll(table, [Sample(100, readBytes: 10, userTime: 5 * Ms)], 1).Deltas.ShouldHaveSingleItem();

        delta.IoRead.ShouldBe(0);
        delta.CpuUserMs.ShouldBe(0);
        delta.CpuPct.ShouldBe(0);
    }

    /// <summary>
    /// The reason nothing in AppLedger is keyed on a bare PID. Windows hands PIDs out again; a table keyed
    /// on PID alone would subtract the dead process's counters from the new one's and report either a huge
    /// negative or a plausible small delta that belongs to neither.
    /// </summary>
    [Fact]
    public void Update_PidReuse_IsOneInstanceExitingAndAnotherStarting()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, createTime: 1, readBytes: 9_000)], 0);

        var tick = Poll(table, [Sample(100, createTime: 2, readBytes: 4)], 1);

        tick.Started.ShouldHaveSingleItem().Key.ShouldBe(new ProcessKey(100, 2));
        tick.Exited.ShouldHaveSingleItem().Key.ShouldBe(new ProcessKey(100, 1));
        tick.Deltas.ShouldBeEmpty();
        table.LiveCount.ShouldBe(1);
    }

    [Fact]
    public void Update_InstanceDisappears_IsReportedExitedWithTheNameItHadWhileAlive()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, imageName: "game.exe", parentPid: 42), Sample(200)], 0);

        var tick = Poll(table, [Sample(200)], 1);

        var exit = tick.Exited.ShouldHaveSingleItem();
        exit.Key.Pid.ShouldBe(100);
        exit.ImageName.ShouldBe("game.exe");
        exit.ParentPid.ShouldBe(42);
        table.LiveCount.ShouldBe(1);
    }

    [Fact]
    public void Update_InstanceThatStartedThisTick_ContributesFromTheNextTickOnwards()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100)], 0);

        Poll(table, [Sample(100), Sample(200, readBytes: 7_000)], 1)
            .Deltas.Select(d => d.Key.Pid).ShouldBe([100]);

        Poll(table, [Sample(100), Sample(200, readBytes: 7_100)], 2)
            .Deltas.Single(d => d.Key.Pid == 200).IoRead.ShouldBe(100);
    }

    /// <summary>
    /// Sleep and resume. The wall clock jumps hours while monotonic time advances by one poll interval;
    /// dividing a whole night of counters by one second would report a machine that moved 30 GB in a
    /// second. The bucket is dropped and counters re-baseline (docs/05_COLLECTOR.md §Failure handling).
    /// </summary>
    [Fact]
    public void Update_WallClockJumpsForward_DropsTheIntervalAndRebaselines()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        table.Update([Sample(100, readBytes: 1_000)], 1_700_000_000, TimeSpan.FromSeconds(0));
        table.Update([Sample(100, readBytes: 1_100)], 1_700_000_001, TimeSpan.FromSeconds(1));

        // Eight hours of wall clock, one second of monotonic time.
        var tick = table.Update([Sample(100, readBytes: 40_000_000_000)], 1_700_028_802, TimeSpan.FromSeconds(2));

        tick.Rebaselined.ShouldBeTrue();
        tick.Deltas.ShouldBeEmpty();

        // The next honest interval measures from the post-jump reading, not from before it.
        var after = table.Update([Sample(100, readBytes: 40_000_000_500)], 1_700_028_803, TimeSpan.FromSeconds(3));
        after.Rebaselined.ShouldBeFalse();
        after.Deltas.ShouldHaveSingleItem().IoRead.ShouldBe(500);
    }

    /// <summary>A manual time change backwards is the same problem in the other direction.</summary>
    [Fact]
    public void Update_WallClockJumpsBackward_DropsTheInterval()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        table.Update([Sample(100)], 1_700_000_000, TimeSpan.FromSeconds(0));
        table.Update([Sample(100)], 1_700_000_001, TimeSpan.FromSeconds(1));

        table.Update([Sample(100)], 1_699_999_000, TimeSpan.FromSeconds(2)).Rebaselined.ShouldBeTrue();
    }

    /// <summary>Drift inside the tolerance is ordinary scheduling jitter, not a jump.</summary>
    [Fact]
    public void Update_SmallDriftWithinTolerance_IsNotTreatedAsAJump()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        table.Update([Sample(100, readBytes: 100)], 1_700_000_000, TimeSpan.FromSeconds(0));
        table.Update([Sample(100, readBytes: 200)], 1_700_000_001, TimeSpan.FromSeconds(1));

        // Four seconds of wall clock against one of monotonic: under the five-second tolerance.
        var tick = table.Update([Sample(100, readBytes: 500)], 1_700_000_005, TimeSpan.FromSeconds(2));

        tick.Rebaselined.ShouldBeFalse();
        tick.Deltas.ShouldHaveSingleItem().IoRead.ShouldBe(300);
    }

    [Fact]
    public void Update_ZeroLengthInterval_ProducesNoDeltas()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        table.Update([Sample(100)], 1_700_000_000, TimeSpan.FromSeconds(1));

        table.Update([Sample(100)], 1_700_000_000, TimeSpan.FromSeconds(1)).Rebaselined.ShouldBeTrue();
    }

    [Fact]
    public void Reset_ForgetsEverything_SoTheNextPollStartsClean()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100), Sample(200)], 0);
        Poll(table, [Sample(100), Sample(200)], 1);

        table.Reset();
        table.LiveCount.ShouldBe(0);

        var tick = Poll(table, [Sample(100)], 2);
        tick.Rebaselined.ShouldBeTrue();
        tick.Started.ShouldHaveSingleItem();
        tick.Exited.ShouldBeEmpty();
    }

    [Fact]
    public void Update_EmptySnapshot_ExitsEveryKnownInstance()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100), Sample(200)], 0);

        var tick = Poll(table, [], 1);

        tick.Exited.Select(e => e.Key.Pid).Order().ShouldBe([100, 200]);
        table.LiveCount.ShouldBe(0);
    }

    /// <summary>
    /// The session id rides along untouched, because the own-session privacy filter of
    /// docs/12_PRIVACY_AND_RETENTION.md is applied by the source, and the snapshot step needs to know.
    /// </summary>
    [Fact]
    public void Update_CarriesImageNameAndSessionThroughToTheDelta()
    {
        var table = new ProcessTable(logicalCpuCount: 4);
        Poll(table, [Sample(100, imageName: "chrome.exe", sessionId: 3)], 0);

        var delta = Poll(table, [Sample(100, imageName: "chrome.exe", sessionId: 3)], 1)
            .Deltas.ShouldHaveSingleItem();

        delta.ImageName.ShouldBe("chrome.exe");
        delta.SessionId.ShouldBe(3);
    }
}
