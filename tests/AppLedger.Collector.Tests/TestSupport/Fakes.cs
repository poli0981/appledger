using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Policy;

namespace AppLedger.Collector.Tests.TestSupport;

/// <summary>
/// A policy over fixture roots. Real known folders would make these tests depend on the machine they run
/// on, and the point of the pipeline tests is that they do not (docs/19_TESTING.md §Layers).
/// </summary>
internal sealed class FakePolicyGuard : IPolicyGuard
{
    internal const string WindowsRoot = @"C:\Windows";
    internal const string DataRoot = @"C:\Users\fixture\AppData\Local\AppLedgerData";

    internal static IReadOnlyList<string> Boundaries { get; } =
    [
        @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Users\fixture\AppData\Local",
        @"C:\Users\fixture\AppData\Roaming", @"C:\Users\fixture", @"C:\ProgramData", WindowsRoot,
    ];

    private readonly PathTierTable _paths = new(
        protectedOsRoots: [WindowsRoot, @"C:\Program Files\WindowsApps"],
        sensitiveRoots: [],
        sensitiveGlobs: [],
        dataRoot: DataRoot);

    private readonly ProcessTierTable _processes = new();

    public PathDecision Evaluate(string? rawPath)
    {
        if (!PathRules.TryNormalize(rawPath, out var normalized, out var reason))
        {
            return PathDecision.Rejected(reason);
        }

        var tier = _paths.Classify(normalized, out var tierReason);
        return new PathDecision(normalized, tier, tier >= PathTier.WriteProtected, tierReason, Unresolved: false);
    }

    public PathTier TierOf(string canonicalPath) => _paths.Classify(canonicalPath, out _);

    public bool CanScan(string canonicalPath) => _paths.CanScan(canonicalPath);

    public ProcessTier TierOfProcess(string? canonicalImagePath, string? imageFileName) =>
        _processes.Classify(canonicalImagePath, imageFileName);

    public bool IsInsideDataRoot(string canonicalPath) => _paths.IsInsideDataRoot(canonicalPath);
}

/// <summary>
/// An enricher backed by a fixture table, which also counts calls — so a test can assert that a Tier-2
/// instance was never enriched, rather than inferring it from null fields.
/// </summary>
internal sealed class FakeProcessEnricher : IProcessEnricher
{
    private readonly Dictionary<ProcessKey, string> _imagePaths = [];

    internal List<ProcessKey> Enriched { get; } = [];

    internal FakeProcessEnricher WithImagePath(ProcessKey key, string imagePath)
    {
        _imagePaths[key] = imagePath;
        return this;
    }

    public ProcessEnrichment Enrich(ProcessKey key, ProcessTier tier)
    {
        if (tier == ProcessTier.ZeroTouch)
        {
            return ProcessEnrichment.Unavailable;
        }

        Enriched.Add(key);

        return _imagePaths.TryGetValue(key, out var path)
            ? new ProcessEnrichment { Attempted = true, ImagePath = path }
            : new ProcessEnrichment { Attempted = true };
    }
}

/// <summary>
/// An ETW source whose events are raised by the test rather than by a session, which is the whole point of
/// the handler seam: the same code path runs against a live session, a recorded .etl and this
/// (docs/19_TESTING.md §Layers).
/// </summary>
internal sealed class FakeEtwSource : IEtwSource
{
    public string Name => "EtwHub";

    public SensorHealth Health { get; set; } = new(SensorState.Running);

    public long EventsLost { get; set; }

    public event Action<NetworkEvent>? Network;

    public event Action<DiskIoEvent>? DiskIo;

    public event Action<DnsEvent>? Dns;

    public event Action<ImageLoadEvent>? ImageLoad;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    internal void Raise(in NetworkEvent e) => Network?.Invoke(e);

    internal void Raise(in DiskIoEvent e) => DiskIo?.Invoke(e);

    internal void Raise(in DnsEvent e) => Dns?.Invoke(e);

    internal void RaiseImageLoad(in ImageLoadEvent e) => ImageLoad?.Invoke(e);
}

/// <summary>
/// A GPU source returning whatever the test last set. Its health matters as much as its samples: a machine
/// with no WDDM 2.x counters is a normal Tuesday, not a fault (docs/01 §Degraded modes).
/// </summary>
internal sealed class FakeGpuSource : IGpuSource
{
    private IReadOnlyList<GpuSample> _samples = [];

    public string Name => "GpuPoller";

    public SensorHealth Health { get; set; } = new(SensorState.Running);

    internal int SampleCalls { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyList<GpuSample> Sample()
    {
        SampleCalls++;
        return _samples;
    }

    internal FakeGpuSource Returning(params GpuSample[] samples)
    {
        _samples = samples;
        return this;
    }
}
