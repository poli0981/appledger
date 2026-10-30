namespace AppLedger.Core.Identity;

/// <summary>
/// Supplies the <see cref="IdentityIndexes"/> snapshot the resolver reads, and rebuilds it when the machine
/// changes underneath it.
/// </summary>
/// <remarks>
/// A port because building one means reading the Uninstall registry and walking launcher manifest
/// directories, which is Windows work and belongs in Infrastructure — while the resolver that consumes it
/// is pure Core and the S2 fixtures supply their own snapshot as data.
/// <para>
/// <b><see cref="Refresh"/> is expected to be cheap when nothing is due.</b> It is called from the
/// collector's tick loop, which runs on a budget (docs/05_COLLECTOR.md §Budget), so an implementation that
/// re-reads the registry on every call is wrong: the rate limiting belongs behind this interface, where the
/// implementation knows what its own rebuild costs.
/// </para>
/// </remarks>
public interface IIdentityIndexSource
{
    /// <summary>
    /// The current snapshot. Never null and never partially built: a rebuild that fails leaves the previous
    /// snapshot in place, because resolving against stale indexes is better than resolving against none.
    /// </summary>
    IdentityIndexes Current { get; }

    /// <summary>
    /// Rebuilds the snapshot if one is due, and reports whether <see cref="Current"/> changed.
    /// </summary>
    /// <returns>
    /// True when a new snapshot replaced the old one. The caller decides what that is worth — notably
    /// whether to call <see cref="IIdentityResolver.Invalidate"/>, which re-resolves live instances and is
    /// not always the right answer (docs/03_APP_IDENTITY.md §Caching &amp; invalidation).
    /// </returns>
    bool Refresh();
}
