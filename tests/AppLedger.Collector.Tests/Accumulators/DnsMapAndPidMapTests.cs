using System.Net;
using AppLedger.Collector.Accumulators;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Accumulators;

/// <summary>
/// The two bounded lookup structures the ETW handlers depend on. Both exist to stay a fixed size no matter
/// how long the Agent runs (docs/05_COLLECTOR.md §Budget controls).
/// </summary>
public sealed class DnsMapTests
{
    [Fact]
    public void Learn_ThenLookup_ReturnsTheHost()
    {
        var map = new DnsMap();

        map.Learn(IPAddress.Parse("93.184.216.34"), "example.com", 1_700_000_000);

        map.Lookup("93.184.216.34").ShouldBe("example.com");
    }

    [Fact]
    public void Lookup_AddressNeverSeen_IsNull() => new DnsMap().Lookup("1.2.3.4").ShouldBeNull();

    [Fact]
    public void Learn_SameAddressAgain_UpdatesRatherThanDuplicating()
    {
        var map = new DnsMap();

        map.Learn("93.184.216.34", "old.example.com", 1_700_000_000);
        map.Learn("93.184.216.34", "new.example.com", 1_700_000_100);

        map.Count.ShouldBe(1);
        map.Lookup("93.184.216.34").ShouldBe("new.example.com");
    }

    /// <summary>The cap is a memory decision, and it must hold under a machine that resolves constantly.</summary>
    [Fact]
    public void Learn_BeyondCapacity_EvictsTheLeastRecentlyUsed()
    {
        var map = new DnsMap(capacity: 3);

        map.Learn("1.1.1.1", "a.example", 1);
        map.Learn("2.2.2.2", "b.example", 2);
        map.Learn("3.3.3.3", "c.example", 3);

        // Touching the oldest makes it the newest, so the next eviction takes b instead.
        map.Lookup("1.1.1.1").ShouldBe("a.example");
        map.Learn("4.4.4.4", "d.example", 4);

        map.Count.ShouldBe(3);
        map.Lookup("2.2.2.2").ShouldBeNull();
        map.Lookup("1.1.1.1").ShouldBe("a.example");
        map.Evicted.ShouldBe(1);
    }

    /// <summary>
    /// An evicted address shows as an IP rather than as a wrong name. That is the honest degradation: an
    /// unnamed address is a fact, a stale name would not be.
    /// </summary>
    [Fact]
    public void Learn_EvictedAddress_BecomesUnlabelledRatherThanWrong()
    {
        var map = new DnsMap(capacity: 1);
        map.Learn("1.1.1.1", "a.example", 1);

        map.Learn("2.2.2.2", "b.example", 2);

        map.Lookup("1.1.1.1").ShouldBeNull();
    }

    [Fact]
    public void Learn_EmptyHost_IsIgnored()
    {
        var map = new DnsMap();

        map.Learn(IPAddress.Parse("1.1.1.1"), "   ", 1);

        map.Count.ShouldBe(0);
    }

    [Fact]
    public void Snapshot_ReturnsEveryMappingOldestFirst()
    {
        var map = new DnsMap();
        map.Learn("1.1.1.1", "a.example", 1);
        map.Learn("2.2.2.2", "b.example", 2);

        map.Snapshot().Select(e => e.Host).ShouldBe(["a.example", "b.example"]);
    }

    /// <summary>Purge must leave no trace of what was resolved (docs/12 §Purge semantics).</summary>
    [Fact]
    public void Clear_LeavesNothingBehind()
    {
        var map = new DnsMap();
        map.Learn("1.1.1.1", "a.example", 1);

        map.Clear();

        map.Count.ShouldBe(0);
        map.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_ZeroCapacity_IsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new DnsMap(capacity: 0));
}

/// <summary>
/// The PID-to-instance translation the ETW handlers do on every event. It is a flat array rather than a
/// dictionary because it is read ~12 000 times a second (docs/05 §ETW sessions).
/// </summary>
public sealed class PidMapTests
{
    [Fact]
    public void Lookup_AfterSet_ReturnsTheInstance()
    {
        var map = new PidMap();
        var key = new ProcessKey(1234, 999);

        map.Set(key);

        map.Lookup(1234).ShouldBe(key);
    }

    [Fact]
    public void Lookup_UnknownPid_IsNull() => new PidMap().Lookup(4321).ShouldBeNull();

    /// <summary>
    /// A reused PID must resolve to the instance that holds it now, not the one that used to. This is the
    /// same guard the process table applies, enforced again at the point ETW events arrive.
    /// </summary>
    [Fact]
    public void Set_PidReused_ResolvesToTheNewInstance()
    {
        var map = new PidMap();
        map.Set(new ProcessKey(1234, 111));

        map.Set(new ProcessKey(1234, 222));

        map.Lookup(1234)!.Value.CreateTime.ShouldBe(222);
    }

    [Fact]
    public void Clear_ForgetsOnePid()
    {
        var map = new PidMap();
        map.Set(new ProcessKey(1234, 999));

        map.Clear(1234);

        map.Lookup(1234).ShouldBeNull();
    }

    /// <summary>
    /// A PID outside the array must miss rather than corrupt a neighbour. The array is indexed directly for
    /// speed, so the bounds check is the only thing between a stray PID and someone else's counters.
    /// </summary>
    [Theory]
    [InlineData(PidMap.MaxPid)]
    [InlineData(PidMap.MaxPid + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void OutOfRangePid_IsIgnoredRatherThanCorruptingTheArray(int pid)
    {
        var map = new PidMap();

        Should.NotThrow(() => map.Set(new ProcessKey(pid, 1)));
        Should.NotThrow(() => map.Clear(pid));
        map.Lookup(pid).ShouldBeNull();
    }
}

/// <summary>
/// The handler seam. These run the exact code path a live ETW session runs, driven by scripted events -
/// which is what makes byte attribution testable without an elevated box (docs/19 §Layers).
/// </summary>
public sealed class EtwAccumulatorsTests
{
    private static readonly ProcessKey Chrome = new(100, 1);

    private static (EtwAccumulators Accumulators, PidMap Pids) Build()
    {
        var pids = new PidMap();
        pids.Set(Chrome);
        return (new EtwAccumulators(pids, new DnsMap()), pids);
    }

    [Fact]
    public void OnNetwork_KnownPid_AttributesToTheInstance()
    {
        var (accumulators, _) = Build();

        accumulators.OnNetwork(new NetworkEvent(100, 500, NetworkDirection.Inbound, NetworkProtocol.Tcp,
            IPAddress.Parse("8.8.8.8"), 443, 1_700_000_000));

        accumulators.NetworkFor(Chrome)!.InBytes.ShouldBe(500);
        accumulators.UnattributedEvents.ShouldBe(0);
    }

    /// <summary>
    /// A process that started and exited between polls produces events we cannot attribute. Expected and
    /// small — but counted, because a large number means the poller is falling behind.
    /// </summary>
    [Fact]
    public void OnNetwork_UnknownPid_IsCountedRatherThanGuessedAt()
    {
        var (accumulators, _) = Build();

        accumulators.OnNetwork(new NetworkEvent(9999, 500, NetworkDirection.Inbound, NetworkProtocol.Tcp,
            IPAddress.Parse("8.8.8.8"), 443, 1_700_000_000));

        accumulators.UnattributedEvents.ShouldBe(1);
        accumulators.NetworkFor(Chrome).ShouldBeNull();
    }

    [Fact]
    public void OnDiskIo_SplitsReadsFromWritesAndCountsOperations()
    {
        var (accumulators, _) = Build();

        accumulators.OnDiskIo(new DiskIoEvent(100, 4_096, IsWrite: false, DiskNumber: 0, 1_700_000_000));
        accumulators.OnDiskIo(new DiskIoEvent(100, 8_192, IsWrite: true, DiskNumber: 0, 1_700_000_000));

        var disk = accumulators.DiskFor(Chrome)!;
        disk.ReadBytes.ShouldBe(4_096);
        disk.WriteBytes.ShouldBe(8_192);
        disk.Operations.ShouldBe(2);
    }

    /// <summary>
    /// The DNS mapping is learned globally and the asking process is deliberately not recorded with it:
    /// "which app resolved what" is a browsing history (docs/10 §DNS).
    /// </summary>
    [Fact]
    public void OnDns_LearnsEveryAddressWithoutRecordingWhoAsked()
    {
        var (accumulators, _) = Build();

        accumulators.OnDns(new DnsEvent(
            100,
            "example.com",
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")],
            1_700_000_000));

        accumulators.Dns.Lookup("93.184.216.34").ShouldBe("example.com");
        accumulators.Dns.Lookup("2606:2800:220:1:248:1893:25c8:1946").ShouldBe("example.com");
        accumulators.Dns.Count.ShouldBe(2);
    }

    [Fact]
    public void ResetWindow_EmptiesTotalsButKeepsTheInstances()
    {
        var (accumulators, _) = Build();
        accumulators.OnNetwork(new NetworkEvent(100, 500, NetworkDirection.Inbound, NetworkProtocol.Tcp,
            IPAddress.Parse("8.8.8.8"), 443, 1_700_000_000));

        accumulators.ResetWindow();

        accumulators.NetworkFor(Chrome)!.InBytes.ShouldBe(0);
    }

    [Fact]
    public void Forget_DropsTheInstanceAndItsPidMapping()
    {
        var (accumulators, pids) = Build();
        accumulators.OnNetwork(new NetworkEvent(100, 500, NetworkDirection.Inbound, NetworkProtocol.Tcp,
            IPAddress.Parse("8.8.8.8"), 443, 1_700_000_000));

        accumulators.Forget(Chrome);

        accumulators.NetworkFor(Chrome).ShouldBeNull();
        pids.Lookup(100).ShouldBeNull();
    }

    /// <summary>
    /// Concurrency is not incidental here: TraceEvent delivers on its own threads while the snapshot
    /// timer reads. What must hold is that no byte is lost and nothing throws.
    /// </summary>
    [Fact]
    public void OnNetwork_FromManyThreads_LosesNoBytes()
    {
        var (accumulators, _) = Build();
        const int Threads = 8;
        const int PerThread = 5_000;

        Parallel.For(0, Threads, _ =>
        {
            for (var i = 0; i < PerThread; i++)
            {
                accumulators.OnNetwork(new NetworkEvent(100, 1, NetworkDirection.Inbound, NetworkProtocol.Tcp,
                    IPAddress.Parse("8.8.8.8"), 443, 1_700_000_000));
            }
        });

        accumulators.NetworkFor(Chrome)!.InBytes.ShouldBe(Threads * PerThread);
        accumulators.HandlerErrors.ShouldBe(0);
    }
}
