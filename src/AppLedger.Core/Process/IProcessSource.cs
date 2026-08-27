namespace AppLedger.Core.Process;

/// <summary>
/// The system-wide process snapshot, taken once per poll with no per-process handles at all
/// (docs/04_DATA_SOURCES.md §A, ADR-4).
/// </summary>
/// <remarks>
/// A port in Core with its adapter in Infrastructure, so the collector pipeline can be tested with
/// scripted samples and so the "one call, no handles" property is visible in the interface itself: there
/// is no per-PID method here to reach for.
/// </remarks>
public interface IProcessSource
{
    /// <summary>
    /// Takes one snapshot of every process the caller is allowed to see. The returned span is valid until
    /// the next call: the adapter reuses its buffer between polls, which is what keeps the steady state
    /// allocation-free (docs/05_COLLECTOR.md §Budget controls).
    /// </summary>
    /// <param name="sessionId">
    /// When set, only processes in that logon session are returned — the default privacy filter of
    /// docs/12_PRIVACY_AND_RETENTION.md. Null returns every process the snapshot listed.
    /// </param>
    /// <returns>The processes in the snapshot, in the order the system reported them.</returns>
    ReadOnlySpan<RawProcessSample> Snapshot(int? sessionId = null);

    /// <summary>
    /// The session the current process runs in, for callers that want the default filter without asking
    /// Windows themselves.
    /// </summary>
    int CurrentSessionId { get; }
}
