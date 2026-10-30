namespace AppLedger.Core.Identity;

/// <summary>What a user override does (docs/03_APP_IDENTITY.md §User overrides).</summary>
public enum OverrideKind
{
    /// <summary>Map a match onto an existing app, joining the two.</summary>
    Merge,

    /// <summary>Force matching processes out of their app into one of their own.</summary>
    Split,
}

/// <summary>
/// One row of the <c>app_overrides</c> table, as the resolver sees it.
/// </summary>
/// <remarks>
/// An override outranks every other source, which is the whole point: it is the user telling us we got the
/// grouping wrong, and the UI offers it wherever confidence is below the prompt threshold. It is local
/// metadata and never a system change (docs/11_SAFETY_POLICY.md, docs/12_PRIVACY_AND_RETENTION.md).
/// </remarks>
/// <param name="Kind">Merge or split.</param>
/// <param name="Match">
/// An image path, an install root, a script hash, or a command-line fragment. Compared as a whole path when
/// it looks like one and as a substring of the command line otherwise, because that is what the UI can
/// offer from a process list: "everything that was started like this".
/// </param>
/// <param name="Value">The app id matching processes are forced to.</param>
public sealed record IdentityOverride(OverrideKind Kind, string Match, AppId Value);

/// <summary>
/// The mutable half of resolution: the indexes and the user's overrides. Passed as a snapshot so a refresh
/// cannot change the answer halfway through one tick.
/// </summary>
/// <param name="Indexes">Registry, store, launcher and package-manager state.</param>
/// <param name="Overrides">The user's corrections, applied ahead of everything else.</param>
public sealed record IdentityContext(IdentityIndexes Indexes, IReadOnlyList<IdentityOverride> Overrides)
{
    /// <summary>Nothing installed and nothing overridden.</summary>
    public static IdentityContext Empty { get; } = new(IdentityIndexes.Empty, []);
}
