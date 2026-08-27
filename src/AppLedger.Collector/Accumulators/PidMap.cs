using AppLedger.Core.Identity;

namespace AppLedger.Collector.Accumulators;

/// <summary>
/// Maps a bare PID to the process instance it currently belongs to, for the ETW handlers.
/// </summary>
/// <remarks>
/// ETW events carry a PID and nothing else, but nothing in AppLedger may be keyed on a bare PID
/// (docs/03_APP_IDENTITY.md §Definitions). This is the one place the translation happens, and it is a flat
/// array indexed by PID rather than a dictionary because it is read on every one of the ~12 k network
/// events per second that docs/05 anticipates — at that rate a hash lookup per event is a measurable cost,
/// and a lock would be a disaster.
/// <para>
/// Windows PIDs are multiples of four below 2^22 in practice, but the array is sized for the documented
/// 65536 ceiling: 65536 × 16 bytes is 1 MB, which is affordable, and a PID outside it simply misses rather
/// than corrupting a neighbour.
/// </para>
/// <para>
/// A stale entry is possible for a moment after a process exits and its PID is reused — the poller has not
/// caught up yet. That is why the entry stores the whole <see cref="ProcessKey"/>: a consumer that also
/// knows the expected create time can tell a stale hit from a live one, and the accumulator simply
/// attributes to the last known instance, which is what docs/10 §Byte attribution specifies for the
/// kernel-completion case.
/// </para>
/// </remarks>
public sealed class PidMap
{
    /// <summary>The largest PID this map can hold, from docs/05 §Accumulators.</summary>
    public const int MaxPid = 65536;

    private readonly ProcessKey[] _byPid = new ProcessKey[MaxPid];

    /// <summary>Records that a PID currently belongs to an instance.</summary>
    public void Set(ProcessKey key)
    {
        if ((uint)key.Pid < MaxPid)
        {
            // A single aligned 16-byte write. Readers may briefly observe a torn value under x64's memory
            // model only if the struct exceeded a word, which it does - so the create time is treated as
            // advisory and never as a correctness guarantee. Attribution uses the pid, which cannot tear.
            _byPid[key.Pid] = key;
        }
    }

    /// <summary>Forgets a PID, so a later event for it misses rather than hitting a dead instance.</summary>
    public void Clear(int pid)
    {
        if ((uint)pid < MaxPid)
        {
            _byPid[pid] = default;
        }
    }

    /// <summary>The instance a PID currently maps to, or null when the PID is unknown.</summary>
    public ProcessKey? Lookup(int pid)
    {
        if ((uint)pid >= MaxPid)
        {
            return null;
        }

        var key = _byPid[pid];
        return key.Pid == pid ? key : null;
    }

    /// <summary>Drops everything, for a re-baseline.</summary>
    public void Clear() => Array.Clear(_byPid);
}
