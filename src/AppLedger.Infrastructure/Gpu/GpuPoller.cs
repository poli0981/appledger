using System.Globalization;
using AppLedger.Core.Collection;
using Windows.Win32;
using Windows.Win32.System.Performance;

namespace AppLedger.Infrastructure.Gpu;

/// <summary>
/// Reads per-process GPU counters through PDH (docs/04_DATA_SOURCES.md §C).
/// </summary>
/// <remarks>
/// <b>Absent counters are a normal state, not a fault.</b> The <c>GPU Engine</c> counter set exists only on
/// Windows 10 1709+ with a WDDM 2.x driver, so a VM, a server SKU or an old driver simply has none. The
/// poller reports <see cref="SensorState.Unavailable"/> and the UI shows "N/A" — a zero would claim we
/// looked and found no GPU work, which is a different and false statement.
/// <para>
/// <b>English counter names, always.</b> <c>PdhAddEnglishCounter</c> rather than <c>PdhAddCounter</c>: the
/// localized path on a Japanese Windows is not <c>\GPU Engine(...)</c>, and the manual matrix in docs/19
/// has a ja-JP box precisely because that is the kind of thing that only breaks for other people.
/// </para>
/// <para>
/// <b>Cost.</b> The wildcard expansion is the expensive part — roughly 5 ms per 100 instances — so docs/05
/// §Budget controls caps it at once every 10 seconds even though the sample itself runs every 2.
/// </para>
/// <para>
/// <b>One lock, and every raw handle use is inside it.</b> PDH entry points take a raw <c>PDH_HQUERY</c>, so
/// the query's lifetime has to be held still across a call — and <see cref="Sample"/> runs on the collector
/// thread while <see cref="StopAsync"/> and <see cref="Dispose"/> are the host's shutdown path on another.
/// Without the lock there were two ways to lose: the null check on the handle field and the call that used
/// it were separate statements, so a stop in between threw a <c>NullReferenceException</c>; and a stop
/// during a call closed the query under PDH, which is a use-after-close on a handle the OS may already have
/// recycled. Refcounting the handle with <c>DangerousAddRef</c> would have closed the second hole only, and
/// would have let "the sensor has stopped" return while a PDH call was still in flight. A lock closes both
/// and costs a shutdown at most one sample, which is milliseconds (docs/24_ADR.md §Findings).
/// </para>
/// </remarks>
public sealed class GpuPoller : IGpuSource, IDisposable
{
    /// <summary>
    /// Utilization per engine instance. The instance name carries the PID, which is the only way to
    /// attribute GPU work to a process — there is no per-process GPU API.
    /// </summary>
    private const string UtilizationPath = @"\GPU Engine(*)\Utilization Percentage";

    private const string DedicatedPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string SharedPath = @"\GPU Process Memory(*)\Shared Usage";

    private const uint PdhSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCstatusNoObject = 0xC0000BB8;
    private const uint PdhCstatusNoCounter = 0xC0000BB9;

    /// <summary>Guards every use of the raw query handle; see the remarks on the type.</summary>
    private readonly Lock _gate = new();

    private PdhCloseQuerySafeHandle? _query;
    private PDH_HCOUNTER _utilization;
    private PDH_HCOUNTER _dedicated;
    private PDH_HCOUNTER _shared;
    private bool _collectedOnce;

    /// <inheritdoc />
    public string Name => "GpuPoller";

    /// <inheritdoc />
    public SensorHealth Health { get; private set; } = SensorHealth.Stopped;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    private void Start()
    {
        try
        {
            if (PInvoke.PdhOpenQuery(null, 0, out var query) != PdhSuccess)
            {
                Health = SensorHealth.Unavailable("PdhOpenQuery failed");
                return;
            }

            _query = query;

            if (!TryAddCounter(query, UtilizationPath, out _utilization))
            {
                // No GPU Engine counter set: an old driver, a VM, or a server SKU. Expected on plenty of
                // real machines, so it is reported rather than retried.
                StopCore();
                Health = SensorHealth.Unavailable("no GPU counters");
                return;
            }

            // Memory counters are a bonus: a machine can expose engine utilization without them, and losing
            // VRAM numbers is not a reason to lose GPU percentage too.
            TryAddCounter(query, DedicatedPath, out _dedicated);
            TryAddCounter(query, SharedPath, out _shared);

            // Utilization is a rate, and a rate needs two collections before it means anything. The first
            // one happens here so the first Sample the collector asks for is already valid.
            if (PInvoke.PdhCollectQueryData(QueryHandle(query)) != PdhSuccess)
            {
                StopCore();
                Health = SensorHealth.Unavailable("PdhCollectQueryData failed");
                return;
            }

            _collectedOnce = true;
            Health = new SensorHealth(SensorState.Running);
        }
        catch (DllNotFoundException)
        {
            // pdh.dll is present on every supported Windows, but a stripped image is not our problem to fix.
            Health = SensorHealth.Unavailable("pdh.dll not present");
        }
        catch (EntryPointNotFoundException)
        {
            Health = SensorHealth.Unavailable("pdh entry point missing");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            StopCore();
            Health = SensorHealth.Stopped;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Takes the same lock as <see cref="Sample"/>, so it waits for an in-flight read rather than
    /// closing the query underneath one. That is the point: when this returns, nothing is still
    /// reading PDH.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GpuSample> Sample()
    {
        lock (_gate)
        {
            // Null rather than an empty list for the nothing-to-report paths, which are the common ones on a
            // machine with no GPU counters: an empty collection expression on an array target is the cached
            // Array.Empty, while one on a List allocates - every two seconds, for the whole run.
            return SampleLocked() ?? Nothing;
        }
    }

    private static IReadOnlyList<GpuSample> Nothing { get; } = [];

    private List<GpuSample>? SampleLocked()
    {
        // The null check and the use of the handle have to sit in one critical section with the stop.
        // As two separate statements they were a NullReferenceException waiting for a shutdown to land
        // between them, and the local keeps them together by construction rather than by care.
        if (_query is not { } query || !Health.IsRunning || !_collectedOnce)
        {
            return null;
        }

        if (PInvoke.PdhCollectQueryData(QueryHandle(query)) != PdhSuccess)
        {
            return null;
        }

        var utilization = ReadCounter(_utilization);
        if (utilization.Count == 0)
        {
            return null;
        }

        var dedicated = ReadCounter(_dedicated);
        var shared = ReadCounter(_shared);

        var samples = new List<GpuSample>(utilization.Count);
        foreach (var (pid, percent) in utilization)
        {
            samples.Add(new GpuSample(
                pid,
                percent,
                (long)dedicated.GetValueOrDefault(pid),
                (long)shared.GetValueOrDefault(pid)));
        }

        return samples;
    }

    /// <summary>
    /// Reads one wildcard counter and folds its instances down to one value per PID.
    /// </summary>
    /// <remarks>
    /// A process has one instance per GPU engine — 3D, Copy, VideoDecode and so on — and the headline
    /// number is the **maximum** across them, not the sum. That is Task Manager's convention, and it is the
    /// truthful one: a process saturating the 3D engine while the copy engine idles is using the GPU
    /// completely, and summing would report 25 % for a machine that cannot do any more work.
    /// </remarks>
    private static Dictionary<int, double> ReadCounter(PDH_HCOUNTER counter)
    {
        var byPid = new Dictionary<int, double>();
        unsafe
        {
            if (counter.Value is null)
            {
                return byPid;
            }
        }

        uint size = 0;
        uint count = 0;

        var status = PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE, ref size, out count);
        if (status != PdhMoreData || size == 0)
        {
            return byPid;
        }

        var buffer = new byte[size];

        unsafe
        {
            fixed (byte* p = buffer)
            {
                status = PInvoke.PdhGetFormattedCounterArray(
                    counter, PDH_FMT.PDH_FMT_DOUBLE, &size, &count, (PDH_FMT_COUNTERVALUE_ITEM_W*)p);

                if (status != PdhSuccess)
                {
                    return byPid;
                }

                var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)p;
                for (var i = 0u; i < count; i++)
                {
                    var item = &items[i];
                    if (item->FmtValue.CStatus != PdhSuccess)
                    {
                        continue;
                    }

                    var instance = item->szName.ToString();
                    if (!TryParsePid(instance, out var pid))
                    {
                        continue;
                    }

                    var value = item->FmtValue.Anonymous.doubleValue;
                    byPid[pid] = byPid.TryGetValue(pid, out var existing) ? System.Math.Max(existing, value) : value;
                }
            }
        }

        return byPid;
    }

    /// <summary>
    /// Pulls the PID out of a counter instance name such as
    /// <c>pid_1234_luid_0x00000000_0x0000C24E_phys_0_eng_0_engtype_3D</c>.
    /// </summary>
    /// <remarks>
    /// Parsing a counter instance name is not elegant, but there is no per-process GPU API: the instance
    /// name is the only place the PID appears. The prefix is matched exactly so a future instance shape
    /// yields nothing rather than a wrong PID.
    /// </remarks>
    internal static bool TryParsePid(string? instanceName, out int pid)
    {
        pid = 0;

        const string Prefix = "pid_";
        if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = instanceName.AsSpan(Prefix.Length);
        var end = rest.IndexOf('_');
        var digits = end < 0 ? rest : rest[..end];

        return !digits.IsEmpty && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }

    private static bool TryAddCounter(
        PdhCloseQuerySafeHandle query, string path, out PDH_HCOUNTER counter)
    {
        counter = default;

        unsafe
        {
            fixed (char* p = path)
            {
                PDH_HCOUNTER handle;
                var status = PInvoke.PdhAddEnglishCounter(
                    QueryHandle(query), p, 0, &handle);

                if (status is PdhCstatusNoObject or PdhCstatusNoCounter || status != PdhSuccess)
                {
                    return false;
                }

                counter = handle;
                return true;
            }
        }
    }

    /// <summary>
    /// The raw query handle the PDH entry points want. The SafeHandle owns the lifetime and the caller
    /// holds <see cref="_gate"/>, so the handle cannot be closed while the call using it is in flight.
    /// </summary>
    /// <remarks>
    /// It takes the query rather than reading the field, and that is the whole reason it is safe to
    /// call. The old form was <c>_query!</c> - a null-forgiving operator on a field another thread can
    /// clear, which is a promise the type cannot keep. Passing a value the caller already null-checked
    /// under the lock makes the promise one the compiler checks.
    /// </remarks>
    private static PDH_HQUERY QueryHandle(PdhCloseQuerySafeHandle query) =>
        new(query.DangerousGetHandle());

    /// <summary>Closes the query. The caller must hold <see cref="_gate"/>.</summary>
    private void StopCore()
    {
        _query?.Dispose();
        _query = null;
        _utilization = default;
        _dedicated = default;
        _shared = default;
        _collectedOnce = false;
    }
}
