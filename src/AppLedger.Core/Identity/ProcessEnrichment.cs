using AppLedger.Core.Policy;

namespace AppLedger.Core.Identity;

/// <summary>
/// Everything that needs a <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle, gathered once per instance and
/// the handle closed immediately (docs/04_DATA_SOURCES.md §B).
/// </summary>
/// <remarks>
/// Every field is nullable on purpose. A Tier-2 process yields <see cref="Unavailable"/> because we open
/// no handle at all; a PPL process denies individual queries even when the handle succeeds; and a process
/// can exit between the snapshot and the query. Any consumer that assumes a value here is present has a
/// bug that only shows up on the machines we care most about getting right.
/// </remarks>
public readonly record struct ProcessEnrichment
{
    /// <summary>
    /// The result for a process we deliberately did not touch, or could not open. Distinguishable from a
    /// partial result by <see cref="Attempted"/>.
    /// </summary>
    public static ProcessEnrichment Unavailable => default;

    /// <summary>
    /// True when a handle was opened and at least one query ran. False for Tier-2 processes — which is the
    /// property the zero-touch test asserts — and for a process we failed to open.
    /// </summary>
    public bool Attempted { get; init; }

    /// <summary>Full image path, canonicalized. Null when unavailable.</summary>
    public string? ImagePath { get; init; }

    /// <summary>
    /// Full command line. Null when unavailable, which includes PPL processes returning
    /// <c>STATUS_ACCESS_DENIED</c> and the case where command-line storage is disabled by policy.
    /// </summary>
    public string? CommandLine { get; init; }

    /// <summary>MSIX package family name, or null when the process is not packaged.</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>The owning user's SID in string form.</summary>
    public string? UserSid { get; init; }

    /// <summary>The owning user's account name, when the SID resolves.</summary>
    public string? UserName { get; init; }

    /// <summary>Token integrity level.</summary>
    public IntegrityLevel Integrity { get; init; }

    /// <summary>True when the token is elevated. Null when the token could not be read.</summary>
    public bool? Elevated { get; init; }

    /// <summary>Process architecture, e.g. <c>x64</c>, <c>x86</c>, <c>ARM64</c>.</summary>
    public string? Architecture { get; init; }
}

/// <summary>Windows token integrity levels, as reported by <c>TokenIntegrityLevel</c>.</summary>
public enum IntegrityLevel
{
    /// <summary>The token could not be read, or was not attempted.</summary>
    Unknown = 0,

    /// <summary>Untrusted.</summary>
    Untrusted,

    /// <summary>Low, e.g. a browser renderer or a protected-mode process.</summary>
    Low,

    /// <summary>Medium — the level an ordinary user process runs at.</summary>
    Medium,

    /// <summary>High — elevated.</summary>
    High,

    /// <summary>System.</summary>
    System,
}

/// <summary>
/// Fills in the handle-requiring half of <see cref="ProcessFacts"/>.
/// </summary>
/// <remarks>
/// The implementation must return <see cref="ProcessEnrichment.Unavailable"/> for a
/// <see cref="ProcessTier.ZeroTouch"/> process **before** doing anything else. That is not an optimization:
/// docs/11_SAFETY_POLICY.md §Process access tiers means no <c>OpenProcess</c> at all, and the test that
/// proves it counts calls.
/// </remarks>
public interface IProcessEnricher
{
    /// <summary>
    /// Enriches one instance. Never throws for a process that has exited or refuses a query: the affected
    /// fields simply come back null.
    /// </summary>
    /// <param name="key">The instance to enrich.</param>
    /// <param name="tier">The tier <see cref="IPolicyGuard"/> decided before any handle was considered.</param>
    ProcessEnrichment Enrich(ProcessKey key, ProcessTier tier);
}
