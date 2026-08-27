using AppLedger.Core.Collection;
using AppLedger.Infrastructure.Etw;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Etw;

/// <summary>
/// What can be verified about the ETW hub <b>without</b> administrator rights — which is exactly the Lite
/// mode path, and the one a non-elevated CI runner exercises.
/// </summary>
/// <remarks>
/// The real sessions are covered by <see cref="EtwHubAdminTests"/>, which is <c>Category=Admin</c> and
/// excluded from CI (docs/19_TESTING.md §Layers). Those need an elevated terminal on a developer box.
/// </remarks>
public sealed class EtwHubTests
{
    private static EtwHub Build() => new(NullLogger<EtwHub>.Instance, retryDelay: TimeSpan.Zero);

    /// <summary>
    /// Being unelevated is not a fault. Lite mode runs here by design, so the hub must report
    /// <see cref="SensorState.Unavailable"/> with a reason rather than throw — the UI then shows "N/A" and
    /// says why, instead of a zero that means "we could not look" (docs/01 §Degraded modes).
    /// </summary>
    [Fact]
    public async Task StartAsync_WithoutElevation_IsUnavailableRatherThanAnException()
    {
        if (EtwHub.CanCreateSessions)
        {
            // On an elevated box this case cannot be produced; the admin suite covers the other side.
            return;
        }

        using var hub = Build();

        await Should.NotThrowAsync(() => hub.StartAsync());

        hub.Health.State.ShouldBe(SensorState.Unavailable);
        hub.Health.Detail.ShouldBe("not elevated");
        hub.Health.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_IsSafe()
    {
        using var hub = Build();

        await Should.NotThrowAsync(() => hub.StopAsync());

        hub.Health.State.ShouldBe(SensorState.Stopped);
    }

    [Fact]
    public void Dispose_WhenNeverStarted_IsSafe() => Should.NotThrow(() => Build().Dispose());

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var hub = Build();

        hub.Dispose();

        Should.NotThrow(hub.Dispose);
    }

    [Fact]
    public void EventsLost_BeforeStarting_IsZero() => Build().EventsLost.ShouldBe(0);

    /// <summary>
    /// The names are fixed rather than generated so a crashed Agent's session can be found and reclaimed by
    /// name on the next start. A random name would leave an orphaned session consuming one of the eight
    /// system-logger slots until reboot (docs/05 §ETW sessions).
    /// </summary>
    [Fact]
    public void SessionNames_AreFixedAndDistinct()
    {
        EtwHub.KernelSessionName.ShouldBe("AppLedger-Kernel");
        EtwHub.UserSessionName.ShouldBe("AppLedger-User");

        // Not "NT Kernel Logger": that name is a single global session, so taking it would evict whatever
        // other tool on the machine is using it.
        EtwHub.KernelSessionName.ShouldNotBe("NT Kernel Logger");
    }

    [Fact]
    public void Name_IsStableForTheHealthReport() => Build().Name.ShouldBe("EtwHub");
}

/// <summary>
/// The real sessions. Excluded from CI because creating a kernel session needs administrator rights
/// (docs/19_TESTING.md §Layers, "Admin (real sessions)").
/// </summary>
/// <remarks>
/// Run these from an elevated terminal with <c>dotnet test --filter Category=Admin</c>. They are the only
/// executable proof that the session keywords, the reclaim and the lost-event counter are wired correctly;
/// everything above them is a translation test.
/// </remarks>
[Trait("Category", "Admin")]
public sealed class EtwHubAdminTests
{
    private static EtwHub Build() => new(NullLogger<EtwHub>.Instance, retryDelay: TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task StartAsync_OnAnElevatedBox_ReachesRunning()
    {
        EtwHub.CanCreateSessions.ShouldBeTrue("run this from an elevated terminal");

        using var hub = Build();
        await hub.StartAsync();

        try
        {
            hub.Health.State.ShouldBe(SensorState.Running);
        }
        finally
        {
            await hub.StopAsync();
        }
    }

    /// <summary>
    /// The case that makes a restart work, and the one that found a crash the first time it was run for
    /// real: reclaiming a session pulls it out from under the abandoned hub's <c>Process()</c> loop, which
    /// throws — and an exception on a background thread takes the whole process with it unless the loop
    /// guards itself. This test aborting the run instead of failing is what that bug looked like.
    /// </summary>
    [Fact]
    public async Task StartAsync_WithAStaleSessionOfOurOwnName_ReclaimsItWithoutKillingTheProcess()
    {
        EtwHub.CanCreateSessions.ShouldBeTrue("run this from an elevated terminal");

        // Abandoned deliberately: no Stop, no Dispose, exactly the way a crashed Agent leaves its session
        // behind. Cleaning it up here would test the happy path instead of the one that matters.
        var first = Build();
        await first.StartAsync();
        first.Health.State.ShouldBe(SensorState.Running);

        using var second = Build();
        await second.StartAsync();

        try
        {
            second.Health.State.ShouldBe(SensorState.Running);

            // The abandoned hub's loop ended because its session was taken away. It must notice and say so.
            // Note the shape of that ending: a clean external stop makes Process() *return*, it does not
            // throw — so a guard that only caught exceptions would leave this hub reporting Running while
            // collecting nothing, which is the quiet version of the crash this test also covers.
            await WaitUntilAsync(() => first.Health.State != SensorState.Running, TimeSpan.FromSeconds(10));

            first.Health.State.ShouldBe(
                SensorState.Unavailable,
                "a hub whose session was stopped underneath it must stop claiming to be running");
            first.Health.Detail.ShouldNotBeNullOrWhiteSpace("the health report has to say why");
        }
        finally
        {
            first.Dispose();
            await second.StopAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Observes this process's own traffic end to end: session, keyword, handler, translation. If byte
    /// attribution is ever wrong, this is the test that says so.
    /// </summary>
    [Fact]
    public async Task Network_ObservesThisProcessOwnTraffic()
    {
        EtwHub.CanCreateSessions.ShouldBeTrue("run this from an elevated terminal");

        using var hub = Build();
        var seen = new List<NetworkEvent>();
        hub.Network += e =>
        {
            if (e.ProcessId == Environment.ProcessId)
            {
                lock (seen)
                {
                    seen.Add(e);
                }
            }
        };

        await hub.StartAsync();

        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();

            var payload = new byte[64 * 1024];
            await client.GetStream().WriteAsync(payload);
            await client.GetStream().FlushAsync();

            var buffer = new byte[payload.Length];
            await accepted.GetStream().ReadExactlyAsync(buffer);

            // ETW delivery is buffered; give the session a moment to flush.
            await Task.Delay(TimeSpan.FromSeconds(3));

            lock (seen)
            {
                seen.ShouldNotBeEmpty("no network events were attributed to this process");
                seen.ShouldContain(e => e.IsLoopback, "loopback traffic must be flagged as such");
            }
        }
        finally
        {
            await hub.StopAsync();
        }
    }
}
