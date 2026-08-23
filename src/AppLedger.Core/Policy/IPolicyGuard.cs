namespace AppLedger.Core.Policy;

/// <summary>
/// The single authority for path and process access decisions (docs/11_SAFETY_POLICY.md). No other code
/// may classify a path or decide whether a process may be opened.
/// </summary>
/// <remarks>
/// The implementation lives in Infrastructure because full canonicalization needs the file system
/// (<c>GetLongPathNameW</c>, <c>GetFinalPathNameByHandleW</c>) and the known-folder roots. Every pipe
/// request carrying a path is re-checked here inside the Agent, regardless of what the UI already did:
/// the UI's own checks are UX, not security.
/// </remarks>
public interface IPolicyGuard
{
    /// <summary>
    /// Canonicalizes and tiers a raw path. Never throws for bad input: unusable shapes come back as a
    /// rejected <see cref="PathDecision"/> with a reason.
    /// </summary>
    PathDecision Evaluate(string? rawPath);

    /// <summary>
    /// The tier of an already-canonical path. Cheaper than <see cref="Evaluate"/> for paths we produced
    /// ourselves, such as those coming out of the ETW device-path mapper.
    /// </summary>
    PathTier TierOf(string canonicalPath);

    /// <summary>
    /// True when a path may be enumerated as part of a disk scan. Tier-0 roots are never scanned, and a
    /// Tier-1 directory is measured but its entries are never named.
    /// </summary>
    bool CanScan(string canonicalPath);

    /// <summary>
    /// The access tier of a process instance. <see cref="ProcessTier.ZeroTouch"/> means the enrichment
    /// adapter must not call <c>OpenProcess</c> at all — not with reduced rights, not once.
    /// </summary>
    ProcessTier TierOfProcess(string? canonicalImagePath, string? imageFileName);

    /// <summary>
    /// True when the given canonical path is inside the AppLedger data root, the only place we ever write.
    /// </summary>
    bool IsInsideDataRoot(string canonicalPath);
}
