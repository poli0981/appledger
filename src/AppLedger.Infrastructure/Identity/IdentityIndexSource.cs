using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Platform;

namespace AppLedger.Infrastructure.Identity;

/// <summary>
/// The Windows implementation of <see cref="IIdentityIndexSource"/>: the registry, the launcher manifests
/// and the package-manager directories, rebuilt on an interval.
/// </summary>
/// <remarks>
/// <b>Why an interval rather than change notification.</b> docs/03_APP_IDENTITY.md §Caching &amp;
/// invalidation asks for <c>RegNotifyChangeKeyValue</c> on the Uninstall keys and
/// <c>ReadDirectoryChangesW</c> on the manifest directories. This builds the snapshot eagerly at Agent
/// start and then rebuilds it on an interval instead, and the reason is that the eager build is what
/// actually matters: everything already installed is indexed before the first process is resolved, so a
/// refresh only ever has to notice software installed *while the Agent runs* — and such an app's processes
/// start after the refresh that finds it. Notification would reduce the window from minutes to seconds for
/// a case that is already rare, in exchange for a registry-notification thread, a watcher per launcher
/// directory, and the handle and thread budget both cost (docs/05_COLLECTOR.md §Budget). Recorded as a
/// deliberate deviation in docs/24_ADR.md rather than left as a silent shortfall.
/// <para>
/// <b>A refresh does not invalidate live resolutions</b>, and the caller is expected not to either.
/// <see cref="IIdentityResolver.Invalidate"/> drops the cache the resolver keys by
/// <c>(pid, createTime)</c> — including the parent entries adoption reads — so invalidating mid-run would
/// re-resolve children whose parents are no longer cached and lose the grouping it was meant to improve.
/// Invalidation belongs to a catalog update or a user override, where re-resolution is the point.
/// </para>
/// </remarks>
public sealed class IdentityIndexSource : IIdentityIndexSource
{
    /// <summary>How often a rebuild is allowed. Chosen against the reasoning in the remarks above.</summary>
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromMinutes(10);

    private readonly UninstallRegistryReader _uninstall;
    private readonly LauncherManifestReader _launchers;
    private readonly PackageManagerReader _packages;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();

    private IdentityIndexes _current = IdentityIndexes.Empty;
    private long _builtAt = long.MinValue;

    /// <summary>Creates a source over the current machine.</summary>
    /// <param name="policy">Tiers every path before it enters a snapshot.</param>
    /// <param name="folders">The known-folder roots the launchers and package managers hang off.</param>
    /// <param name="clock">Drives the rebuild interval; injected so a test needs no wall clock.</param>
    /// <param name="interval">Overridable for tests; defaults to <see cref="DefaultInterval"/>.</param>
    public IdentityIndexSource(
        IPolicyGuard policy,
        KnownFolders folders,
        IClock clock,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(clock);

        _uninstall = new UninstallRegistryReader(policy);
        _launchers = new LauncherManifestReader(policy, folders);
        _packages = new PackageManagerReader(policy, folders);
        _clock = clock;
        _interval = interval ?? DefaultInterval;
    }

    /// <inheritdoc />
    public IdentityIndexes Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public bool Refresh()
    {
        var now = _clock.UtcNowSeconds();

        lock (_gate)
        {
            if (_builtAt != long.MinValue && now - _builtAt < (long)_interval.TotalSeconds)
            {
                return false;
            }
        }

        // Built outside the lock: reading the registry and walking manifest directories is the slow part,
        // and holding the lock through it would stall every Resolve on the collector thread.
        var built = Build();

        lock (_gate)
        {
            _builtAt = now;

            if (Equivalent(_current, built))
            {
                return false;
            }

            _current = built;
            return true;
        }
    }

    /// <summary>
    /// Builds a snapshot now, regardless of the interval. The Agent calls this once at startup so the first
    /// process it resolves already sees a full index.
    /// </summary>
    public IdentityIndexes BuildNow()
    {
        var built = Build();

        lock (_gate)
        {
            _current = built;
            _builtAt = _clock.UtcNowSeconds();
        }

        return built;
    }

    private IdentityIndexes Build() => new()
    {
        Uninstall = _uninstall.Read(),
        Steam = _launchers.ReadSteam(),
        Epic = _launchers.ReadEpic(),
        Gog = _launchers.ReadGog(),
        Itch = _launchers.ReadItch(),
        Scoop = _packages.ReadScoop(),
        Choco = _packages.ReadChocolatey(),

        // Msix, LoadedDrivers and Services are not read here. The first is unused by the resolver, which
        // takes the package family from the process itself; the other two are ETW- and SCM-derived live
        // signals that belong to the collector rather than to a periodic disk read, and the Tier-2
        // promotion that consumes them is not built yet (docs/03 §What ships).
    };

    /// <summary>
    /// Whether two snapshots say the same thing, by count and by the ids and locations they carry. Cheaper
    /// than it looks — these lists are tens to low hundreds of entries — and it is what keeps a rebuild
    /// that found nothing new from being reported as a change.
    /// </summary>
    private static bool Equivalent(IdentityIndexes left, IdentityIndexes right) =>
        SameLaunchers(left.Steam, right.Steam)
        && SameLaunchers(left.Epic, right.Epic)
        && SameLaunchers(left.Gog, right.Gog)
        && SameLaunchers(left.Itch, right.Itch)
        && SameLaunchers(left.Scoop, right.Scoop)
        && SameLaunchers(left.Choco, right.Choco)
        && SameUninstall(left.Uninstall, right.Uninstall);

    private static bool SameLaunchers(
        IReadOnlyList<LauncherIndexEntry> left, IReadOnlyList<LauncherIndexEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Id, right[i].Id, StringComparison.Ordinal)
                || !string.Equals(left[i].InstallLocation, right[i].InstallLocation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameUninstall(
        IReadOnlyList<UninstallIndexEntry> left, IReadOnlyList<UninstallIndexEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Key, right[i].Key, StringComparison.Ordinal)
                || !string.Equals(left[i].InstallLocation, right[i].InstallLocation, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
