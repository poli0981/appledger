using System.ComponentModel;
using System.Runtime.InteropServices;
using AppLedger.Core.Identity;
using AppLedger.Core.Process;
using AppLedger.Infrastructure.Ntdll;

namespace AppLedger.Infrastructure.Process;

/// <summary>
/// The system-wide process snapshot: one <c>NtQuerySystemInformation</c> call per poll, no handles at all
/// (docs/04_DATA_SOURCES.md §A, ADR-4).
/// </summary>
/// <remarks>
/// <b>Budget.</b> The buffer is allocated once and reused between polls, and thread entries are skipped
/// rather than parsed, so the steady state is one syscall plus one linear pass with no allocation beyond
/// the image-name strings. Growth is bounded at <see cref="MaxBufferBytes"/>; past that the caller is
/// expected to halve its rate (docs/05_COLLECTOR.md §Failure handling).
/// <para>
/// Not thread-safe by design: the returned span points into the shared buffer and is valid only until the
/// next call. One poller owns one instance, which is what the collector's threading model already assumes.
/// </para>
/// </remarks>
public sealed class NtProcessSource : IProcessSource
{
    /// <summary>Enough for roughly 4 000 processes with their threads; one call on any normal machine.</summary>
    private const int InitialBufferBytes = 1 << 20;

    /// <summary>
    /// The ceiling of docs/05 §Failure handling. A machine that needs more than this has a pathological
    /// process count and the poller should slow down rather than keep doubling.
    /// </summary>
    internal const int MaxBufferBytes = 64 << 20;

    private byte[] _buffer = new byte[InitialBufferBytes];
    private RawProcessSample[] _samples = new RawProcessSample[512];

    /// <inheritdoc />
    public int CurrentSessionId { get; } = GetCurrentSessionId();

    /// <summary>
    /// True once the buffer hit <see cref="MaxBufferBytes"/> without fitting. The collector reads this to
    /// decide whether to halve its poll rate.
    /// </summary>
    public bool BufferCeilingReached { get; private set; }

    /// <inheritdoc />
    public ReadOnlySpan<RawProcessSample> Snapshot(int? sessionId = null)
    {
        var length = Query();
        if (length == 0)
        {
            return ReadOnlySpan<RawProcessSample>.Empty;
        }

        return Parse(length, sessionId);
    }

    /// <summary>
    /// Fills <see cref="_buffer"/>, growing it when the system says it is too small. Returns the number of
    /// bytes written, or zero when the call could not be satisfied at all.
    /// </summary>
    private unsafe uint Query()
    {
        while (true)
        {
            uint returnLength;
            int status;

            fixed (byte* p = _buffer)
            {
                status = NtDll.NtQuerySystemInformation(
                    NtDll.SystemProcessInformation, p, (uint)_buffer.Length, out returnLength);
            }

            if (NtDll.Succeeded(status))
            {
                return returnLength == 0 ? (uint)_buffer.Length : returnLength;
            }

            if (status != NtDll.StatusInfoLengthMismatch)
            {
                throw new Win32Exception(status, $"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}.");
            }

            if (_buffer.Length >= MaxBufferBytes)
            {
                BufferCeilingReached = true;
                return 0;
            }

            // The returned length is a snapshot of a moving target, so growing to exactly it would often
            // mismatch again on the next call. Doubling converges in a couple of iterations and then stays.
            var wanted = Math.Max((int)returnLength, _buffer.Length * 2);
            _buffer = new byte[Math.Min(wanted, MaxBufferBytes)];
        }
    }

    private unsafe ReadOnlySpan<RawProcessSample> Parse(uint length, int? sessionId)
    {
        var count = 0;

        fixed (byte* origin = _buffer)
        {
            var offset = 0u;

            while (true)
            {
                if (offset + (uint)sizeof(SystemProcessInformation) > length)
                {
                    break;
                }

                var entry = (SystemProcessInformation*)(origin + offset);

                if (sessionId is null || (int)entry->SessionId == sessionId.Value)
                {
                    if (count == _samples.Length)
                    {
                        Array.Resize(ref _samples, _samples.Length * 2);
                    }

                    _samples[count++] = ToSample(entry);
                }

                if (entry->NextEntryOffset == 0)
                {
                    break;
                }

                offset += entry->NextEntryOffset;
            }
        }

        return _samples.AsSpan(0, count);
    }

    private static unsafe RawProcessSample ToSample(SystemProcessInformation* entry) => new()
    {
        Key = new ProcessKey((int)entry->UniqueProcessId, entry->CreateTime),
        ImageName = ReadImageName(entry->ImageName),
        ParentPid = (int)entry->InheritedFromUniqueProcessId,
        SessionId = (int)entry->SessionId,
        UserTime = entry->UserTime,
        KernelTime = entry->KernelTime,
        CycleTime = entry->CycleTime,
        WorkingSetPrivate = entry->WorkingSetPrivateSize,
        WorkingSet = (long)entry->WorkingSetSize,
        PeakWorkingSet = (long)entry->PeakWorkingSetSize,
        PagefileUsage = (long)entry->PagefileUsage,
        PeakPagefileUsage = (long)entry->PeakPagefileUsage,
        HandleCount = (int)entry->HandleCount,
        ThreadCount = (int)entry->NumberOfThreads,
        HardFaultCount = entry->HardFaultCount,
        ReadTransferCount = entry->ReadTransferCount,
        WriteTransferCount = entry->WriteTransferCount,
        OtherTransferCount = entry->OtherTransferCount,
        ReadOperationCount = entry->ReadOperationCount,
        WriteOperationCount = entry->WriteOperationCount,
        BasePriority = entry->BasePriority,
    };

    /// <summary>
    /// The image name points into the same buffer we own, so it has to be copied out. The idle process has
    /// a null buffer, which is the one case that is not an error.
    /// </summary>
    private static unsafe string ReadImageName(UnicodeString name) =>
        name.Buffer == 0 || name.Length == 0
            ? string.Empty
            : new string((char*)name.Buffer, 0, name.Length / sizeof(char));

    private static int GetCurrentSessionId()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return current.SessionId;
    }
}
