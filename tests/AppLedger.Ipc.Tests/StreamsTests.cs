using System.Text;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Ipc.Framing;
using AppLedger.Ipc.Streams;
using Shouldly;
using Xunit;

namespace AppLedger.Ipc.Tests;

/// <summary>
/// The 1 Hz table and the fan-out that carries it to several UI windows at once.
/// </summary>
public sealed class StreamsTests
{
    private static AppSample Sample(
        string appId,
        int procs = 1,
        double cpu = 0,
        long wsPrivate = 0,
        double gpu = 0,
        long netIn = 0) => new()
        {
            AppId = AppId.Parse(appId),
            TsUtc = 1_700_000_001,
            Procs = procs,
            CpuPct = cpu,
            WsPrivate = wsPrivate,
            GpuPct = gpu,
            NetIn = netIn,
        };

    private static byte[] TickBytes(long ts, params AppSample[] samples) =>
        FrameWriter.Prepare(json => AppsTick.Write(json, ts, samples));

    private static string TickJson(long ts, params AppSample[] samples) =>
        Encoding.UTF8.GetString(TickBytes(ts, samples).AsSpan(4));

    // -- AppsTick ----------------------------------------------------------------------------------------

    [Fact]
    public void Write_ProducesTheDocumentedCompactShape()
    {
        var json = TickJson(1_700_000_001, Sample("cat:discord", procs: 4, cpu: 1.2, wsPrivate: 412_345_678, netIn: 1_200));

        json.ShouldBe("""{"ts":1700000001,"cols":["appId","procs","cpu","wsPrivate","gpu","diskR","diskW","netIn","netOut"],"rows":[["cat:discord",4,1.2,412345678,0,0,0,1200,0]]}""");
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryCellType()
    {
        var bytes = TickBytes(
            1_700_000_001,
            Sample("cat:chrome", procs: 40, cpu: 12.5, wsPrivate: 9_000_000_000, gpu: 3.5, netIn: 77),
            Sample("cat:discord", procs: 4));

        var rows = new List<AppRow>();
        AppsTick.Read(bytes.AsSpan(4), rows).ShouldBe(1_700_000_001);

        rows.Count.ShouldBe(2);
        rows[0].AppId.ShouldBe("cat:chrome");
        rows[0].Procs.ShouldBe(40);
        rows[0].CpuPct.ShouldBe(12.5);
        rows[0].WsPrivate.ShouldBe(9_000_000_000);
        rows[0].GpuPct.ShouldBe(3.5);
        rows[0].NetIn.ShouldBe(77);
        rows[1].AppId.ShouldBe("cat:discord");
    }

    /// <summary>
    /// The same rounding the rollup applies before storing. Without it the number the grid shows and the
    /// number the History page shows for the same minute differ in the last place, and a user who notices
    /// is right to distrust both.
    /// </summary>
    [Fact]
    public void Write_Percentages_AreRoundedTheWayTheRollupRoundsThem()
    {
        var json = TickJson(1, Sample("cat:x", cpu: 1.0d / 3.0d, gpu: 2.0d / 3.0d));

        json.ShouldContain("0.3");
        json.ShouldContain("0.7");
        json.ShouldNotContain("0.33333");
    }

    [Fact]
    public void Read_ListIsReusedAcrossTicks_WithoutLeavingOldRowsBehind()
    {
        var rows = new List<AppRow>();

        AppsTick.Read(TickBytes(1, Sample("cat:a"), Sample("cat:b")).AsSpan(4), rows);
        rows.Count.ShouldBe(2);

        AppsTick.Read(TickBytes(2, Sample("cat:c")).AsSpan(4), rows);
        rows.Count.ShouldBe(1);
        rows[0].AppId.ShouldBe("cat:c");
    }

    /// <summary>
    /// A newer server may append a column, which docs/07 §Versioning allows. An older client has to keep
    /// reading the ones it knows rather than misaligning every cell after the new one.
    /// </summary>
    [Fact]
    public void Read_ServerAppendedAnUnknownColumn_StillReadsTheKnownOnes()
    {
        var json = """
            {"ts":1700000001,
             "cols":["appId","procs","cpu","wsPrivate","gpu","diskR","diskW","netIn","netOut","vram"],
             "rows":[["cat:chrome",40,12.5,9000,3.5,1,2,77,88,4096]]}
            """;

        var rows = new List<AppRow>();
        AppsTick.Read(Encoding.UTF8.GetBytes(json), rows).ShouldBe(1_700_000_001);

        var row = rows.ShouldHaveSingleItem();
        row.AppId.ShouldBe("cat:chrome");
        row.NetIn.ShouldBe(77);
        row.NetOut.ShouldBe(88);
    }

    /// <summary>A client that knows a column the server does not send must not read a neighbour's value.</summary>
    [Fact]
    public void Read_ServerOmittedAColumn_LeavesItAtDefault()
    {
        var json = """
            {"ts":5,"cols":["appId","procs","netIn"],"rows":[["cat:chrome",3,900]]}
            """;

        var rows = new List<AppRow>();
        AppsTick.Read(Encoding.UTF8.GetBytes(json), rows).ShouldBe(5);

        var row = rows.ShouldHaveSingleItem();
        row.Procs.ShouldBe(3);
        row.NetIn.ShouldBe(900);
        row.WsPrivate.ShouldBe(0);
        row.GpuPct.ShouldBe(0);
    }

    /// <summary>
    /// Rows without a column header cannot be interpreted, so that is a rejection. A tick with no rows at
    /// all is a different thing — it is an empty second, which is a legitimate state at logon before
    /// anything has been sampled, and rejecting it would make a well-behaved server look broken.
    /// </summary>
    [Theory]
    [InlineData("""{"ts":1,"rows":[["cat:x",1,0,0,0,0,0,0,0]]}""")]
    [InlineData("[]")]
    [InlineData("""{"ts":"soon"}""")]
    public void Read_TickThatCannotBeInterpreted_IsRejected(string json) =>
        AppsTick.Read(Encoding.UTF8.GetBytes(json), []).ShouldBe(-1);

    [Fact]
    public void Read_TickWithNoRows_IsAnEmptySecondRatherThanAnError()
    {
        var rows = new List<AppRow> { new() { AppId = "stale" } };

        AppsTick.Read(TickBytes(1_700_000_002).AsSpan(4), rows).ShouldBe(1_700_000_002);

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// The tick reader parses bytes the peer chose, so it has to be total. <c>TryGetInt64</c> throws
    /// <c>InvalidOperationException</c> on a string token despite its name, which is how this was found.
    /// </summary>
    [Fact]
    public void Read_ArbitraryBytes_NeverThrows()
    {
        var valid = TickBytes(1_700_000_001, Sample("cat:chrome", procs: 4, cpu: 1.5, netIn: 90)).AsSpan(4).ToArray();
        var random = new Random(20260828);
        var rows = new List<AppRow>();

        for (var i = 0; i < 2_000; i++)
        {
            var mutated = valid.AsSpan(0, random.Next(0, valid.Length + 1)).ToArray();
            if (mutated.Length > 0 && random.Next(2) == 0)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            AppsTick.Read(mutated, rows);
        }
    }

    [Theory]
    [InlineData("""{"ts":1,"cols":["appId","procs"],"rows":[["cat:x","many"]]}""")]
    [InlineData("""{"ts":1,"cols":["appId","cpu"],"rows":[["cat:x",{}]]}""")]
    [InlineData("""{"ts":1,"cols":"appId","rows":[]}""")]
    public void Read_CellOfTheWrongType_IsRejectedRatherThanThrown(string json) =>
        AppsTick.Read(Encoding.UTF8.GetBytes(json), []).ShouldBe(-1);

    /// <summary>
    /// This is the 1 Hz hot path for every app on the machine. A serializable DTO would box every number of
    /// every row; the hand-written writer must not quietly reintroduce that.
    /// </summary>
    [Fact]
    public void Write_ManyRows_DoesNotAllocatePerCell()
    {
        var samples = Enumerable.Range(0, 100)
            .Select(i => Sample($"cat:app{i}", procs: i, cpu: i / 3.0, wsPrivate: i * 1_000_000L, netIn: i))
            .ToArray();

        _ = TickBytes(1, samples);   // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = TickBytes(1, samples);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Two buffers and the frame array for a hundred apps. Boxing nine cells per row would add at least
        // 900 allocations on top, so the ceiling here is generous and still decisive.
        allocated.ShouldBeLessThan(64 * 1024);
    }

    // -- StreamHub ---------------------------------------------------------------------------------------

    [Fact]
    public void Publish_EverySubscriber_ReceivesTheSameFrame()
    {
        var hub = new StreamHub();
        using var a = hub.Subscribe(StreamHub.AppsStream);
        using var b = hub.Subscribe(StreamHub.AppsStream);
        using var c = hub.Subscribe(StreamHub.AppsStream);

        var frame = TickBytes(1, Sample("cat:chrome"));
        hub.Publish(StreamHub.AppsStream, frame).ShouldBe(3);

        foreach (var subscription in (StreamSubscription[])[a, b, c])
        {
            subscription.Reader.TryRead(out var received).ShouldBeTrue();
            received.ToArray().ShouldBe(frame);
        }
    }

    /// <summary>
    /// The collector's live channel is a queue, not a broadcast: four readers on it would each get a
    /// disjoint subset of ticks and every one of them would draw a wrong chart. This is what replaces it.
    /// </summary>
    [Fact]
    public void Publish_TwoSubscribers_DoNotStealFramesFromEachOther()
    {
        var hub = new StreamHub();
        using var a = hub.Subscribe(StreamHub.AppsStream);
        using var b = hub.Subscribe(StreamHub.AppsStream);

        hub.Publish(StreamHub.AppsStream, TickBytes(1, Sample("cat:x")));

        a.Reader.TryRead(out _).ShouldBeTrue();
        b.Reader.TryRead(out _).ShouldBeTrue();
    }

    [Fact]
    public void Publish_OtherStream_DoesNotReachThisSubscriber()
    {
        var hub = new StreamHub();
        using var apps = hub.Subscribe(StreamHub.AppsStream);

        hub.Publish(StreamHub.HealthStream, TickBytes(1, Sample("cat:x"))).ShouldBe(0);

        apps.Reader.TryRead(out _).ShouldBeFalse();
    }

    /// <summary>
    /// A subscriber one tick behind wants the newest tick, not a replay of seconds it no longer cares about.
    /// </summary>
    [Fact]
    public void Publish_SubscriberNotReading_DropsOldestAndKeepsTheNewest()
    {
        var hub = new StreamHub();
        using var slow = hub.Subscribe(StreamHub.AppsStream);

        for (var i = 1; i <= 5; i++)
        {
            hub.Publish(StreamHub.AppsStream, TickBytes(i, Sample("cat:x", procs: i)));
        }

        slow.Dropped.ShouldBe(3);
        hub.Dropped.ShouldBe(3);

        var buffered = new List<AppRow>();
        var seen = new List<long>();
        while (slow.Reader.TryRead(out var frame))
        {
            seen.Add(AppsTick.Read(frame.Span[4..], buffered));
        }

        // The two most recent, in order.
        seen.ShouldBe([4L, 5L]);
    }

    /// <summary>
    /// Not tidiness: the server accepts four clients, and one that has stopped reading holds a quarter of
    /// the user's own capacity for the lifetime of the Agent.
    /// </summary>
    [Fact]
    public void Publish_SubscriberDroppingAWholeMinute_IsMarkedWedged()
    {
        var hub = new StreamHub();
        using var wedged = hub.Subscribe(StreamHub.AppsStream);

        var frame = TickBytes(1, Sample("cat:x"));
        for (var i = 0; i < StreamHub.MailboxCapacity + StreamHub.DisconnectAfterDroppedTicks; i++)
        {
            hub.Publish(StreamHub.AppsStream, frame);
        }

        wedged.IsWedged.ShouldBeTrue();
    }

    [Fact]
    public void Publish_SubscriberThatCatchesUp_StopsBeingWedged()
    {
        var hub = new StreamHub();
        using var subscriber = hub.Subscribe(StreamHub.AppsStream);

        var frame = TickBytes(1, Sample("cat:x"));
        for (var i = 0; i < 70; i++)
        {
            hub.Publish(StreamHub.AppsStream, frame);
        }

        subscriber.IsWedged.ShouldBeTrue();

        while (subscriber.Reader.TryRead(out _))
        {
            // Drain, the way a client that came back would.
        }

        hub.Publish(StreamHub.AppsStream, frame);
        subscriber.IsWedged.ShouldBeFalse();
    }

    [Fact]
    public void Dispose_Subscription_Unsubscribes()
    {
        var hub = new StreamHub();
        var subscription = hub.Subscribe(StreamHub.AppsStream);
        hub.SubscriberCount(StreamHub.AppsStream).ShouldBe(1);

        subscription.Dispose();

        hub.SubscriberCount(StreamHub.AppsStream).ShouldBe(0);
        hub.Publish(StreamHub.AppsStream, TickBytes(1, Sample("cat:x"))).ShouldBe(0);
    }

    [Fact]
    public async Task CompleteAll_EndsEveryReaderLoop()
    {
        var hub = new StreamHub();
        using var subscriber = hub.Subscribe(StreamHub.AppsStream);

        hub.CompleteAll();

        var received = 0;
        await foreach (var _ in subscriber.Reader.ReadAllAsync())
        {
            received++;
        }

        received.ShouldBe(0);
    }
}
