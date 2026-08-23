using System.Globalization;

namespace AppLedger.Core.Identity;

/// <summary>
/// The only valid process key: a PID paired with the process creation time.
/// </summary>
/// <param name="Pid">The process id. Reused by Windows, which is exactly why it is never a key on its own.</param>
/// <param name="CreateTime">
/// The 64-bit FILETIME from <c>SYSTEM_PROCESS_INFORMATION.CreateTime</c>, identical to the ETW
/// <c>ProcessStart</c> timestamp within clock resolution (docs/03_APP_IDENTITY.md §Definitions).
/// </param>
public readonly record struct ProcessKey(int Pid, long CreateTime) : IComparable<ProcessKey>
{
    /// <summary>PID 0, the idle process.</summary>
    public static ProcessKey Idle { get; } = new(0, 0);

    /// <summary>
    /// True when this instance could be the parent of <paramref name="child"/> as far as timing goes.
    /// A parent must exist before its child; the check is the PID-reuse guard of docs/03 §Parent adoption.
    /// </summary>
    public bool CouldBeParentOf(ProcessKey child) => CreateTime < child.CreateTime;

    /// <inheritdoc/>
    public int CompareTo(ProcessKey other)
    {
        var byTime = CreateTime.CompareTo(other.CreateTime);
        return byTime != 0 ? byTime : Pid.CompareTo(other.Pid);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Pid}@{CreateTime}");

    public static bool operator <(ProcessKey left, ProcessKey right) => left.CompareTo(right) < 0;

    public static bool operator >(ProcessKey left, ProcessKey right) => left.CompareTo(right) > 0;

    public static bool operator <=(ProcessKey left, ProcessKey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ProcessKey left, ProcessKey right) => left.CompareTo(right) >= 0;
}
