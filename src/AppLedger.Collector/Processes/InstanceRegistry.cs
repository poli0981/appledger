using AppLedger.Core.Identity;
using AppLedger.Core.Policy;

namespace AppLedger.Collector.Processes;

/// <summary>What the collector knows about one live process instance.</summary>
/// <param name="Key">The instance.</param>
/// <param name="AppId">The app it belongs to.</param>
/// <param name="Tier">The access tier decided before any handle was considered.</param>
/// <param name="Resolution">The full resolution, kept so the UI can answer "why is this grouped here?".</param>
public readonly record struct LiveInstance(
    ProcessKey Key,
    AppId AppId,
    ProcessTier Tier,
    ResolutionResult Resolution);

/// <summary>
/// Resolves each process instance to an app exactly once, and remembers the answer for the instance's
/// lifetime (docs/03_APP_IDENTITY.md §Caching &amp; invalidation).
/// </summary>
/// <remarks>
/// Resolving once per instance rather than once per second is not an optimisation, it is the correctness
/// requirement: resolution opens a handle and reads a token, and doing that at 1 Hz for every process on
/// the machine would be exactly the "monitor that opens 200 handles a second" that ADR-4 rules out.
/// <para>
/// The tier is decided from the image name **before** enrichment is attempted, because a Tier-2 process
/// must have no handle opened at all — deciding afterwards would already be too late
/// (docs/11_SAFETY_POLICY.md §Process access tiers).
/// </para>
/// </remarks>
public sealed class InstanceRegistry
{
    private readonly Dictionary<ProcessKey, LiveInstance> _instances = [];
    private readonly IPolicyGuard _policy;
    private readonly IProcessEnricher _enricher;
    private readonly IIdentityResolver _resolver;

    /// <summary>Creates a registry.</summary>
    public InstanceRegistry(IPolicyGuard policy, IProcessEnricher enricher, IIdentityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(enricher);
        ArgumentNullException.ThrowIfNull(resolver);

        _policy = policy;
        _enricher = enricher;
        _resolver = resolver;
    }

    /// <summary>How many instances are currently known.</summary>
    public int Count => _instances.Count;

    /// <summary>Every live instance, for the caller that needs to enumerate apps.</summary>
    public IEnumerable<LiveInstance> Instances => _instances.Values;

    /// <summary>
    /// Folds one tick's lifecycle events in: newly started instances are resolved, exited ones are
    /// forgotten. Returns the instances resolved on this call, which is what creates an <c>apps</c> row.
    /// </summary>
    public IReadOnlyList<LiveInstance> Apply(in ProcessTick tick)
    {
        foreach (var exit in tick.Exited)
        {
            _instances.Remove(exit.Key);
        }

        if (tick.Started.Count == 0)
        {
            return [];
        }

        var resolved = new List<LiveInstance>(tick.Started.Count);
        foreach (var start in tick.Started)
        {
            resolved.Add(Add(start));
        }

        return resolved;
    }

    /// <summary>The instance's app, or null when it was never seen starting.</summary>
    public LiveInstance? Lookup(ProcessKey key) =>
        _instances.TryGetValue(key, out var instance) ? instance : null;

    /// <summary>
    /// Drops every cached resolution so live instances resolve again — the catalog changed, or the user
    /// added an override. History keeps the ids it was written with either way.
    /// </summary>
    public void InvalidateResolutions()
    {
        _resolver.Invalidate();

        // Re-resolving needs the facts, and the only cheap ones we still hold are in the cached results.
        // Re-running enrichment here would mean a burst of handle opens, so the instances are re-resolved
        // from what they already carry and the next process start picks up the new rules in full.
        foreach (var key in _instances.Keys.ToList())
        {
            var previous = _instances[key];
            var facts = new ProcessFacts
            {
                Key = key,
                ImageFileName = previous.Resolution.DisplayName ?? string.Empty,
                ImagePath = previous.Resolution.InstallRoot,
                Tier = previous.Tier,
            };

            var resolution = _resolver.Resolve(facts);
            _instances[key] = previous with { AppId = resolution.AppId, Resolution = resolution };
        }
    }

    private LiveInstance Add(in ProcessLifecycleEvent start)
    {
        // Tier first, from the name alone. Anything else would mean deciding whether to open a handle
        // after having opened one.
        var tier = _policy.TierOfProcess(null, start.ImageName);
        var enrichment = _enricher.Enrich(start.Key, tier);

        var canonicalPath = enrichment.ImagePath is null
            ? null
            : _policy.Evaluate(enrichment.ImagePath).Canonical;

        // A second tier check, now that the real path is known: an anti-cheat directory in the path is a
        // signal the image name alone could not carry.
        if (tier == ProcessTier.Normal && canonicalPath is not null)
        {
            tier = _policy.TierOfProcess(canonicalPath, start.ImageName);
        }

        var facts = new ProcessFacts
        {
            Key = start.Key,
            ImageFileName = start.ImageName,
            SessionId = 0,
            ImagePath = canonicalPath,
            CommandLine = enrichment.CommandLine,
            PackageFamilyName = enrichment.PackageFamilyName,
            Tier = tier,
        };

        var resolution = _resolver.Resolve(facts);
        var instance = new LiveInstance(start.Key, resolution.AppId, tier, resolution);
        _instances[start.Key] = instance;
        return instance;
    }
}
