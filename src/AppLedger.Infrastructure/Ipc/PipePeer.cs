using System.IO.Pipes;
using AppLedger.Infrastructure.Platform;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace AppLedger.Infrastructure.Ipc;

/// <summary>
/// Resolves the process on the other end of a named pipe to a canonical image path, so each side can refuse
/// a peer that is not the executable it expects (docs/07_IPC.md §Transport, ADR-7).
/// </summary>
/// <remarks>
/// This is the defence that <c>CurrentUserOnly</c> was reached for and cannot provide here: any process
/// already running as the user can create a pipe with our name and wait, and the DACL cannot tell one
/// same-user process from another. Comparing image paths can.
/// <para>
/// The handle opened here is <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and nothing else, the same single
/// right AppLedger requests anywhere (ADR-4, docs/11_SAFETY_POLICY.md). It is closed immediately.
/// </para>
/// </remarks>
public static class PipePeer
{
    /// <summary>Reads the client's PID from a connected server pipe.</summary>
    public static uint? ClientProcessId(SafePipeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return PInvoke.GetNamedPipeClientProcessId(handle, out var pid) ? pid : null;
    }

    /// <summary>Reads the server's PID from a connected client pipe.</summary>
    public static uint? ServerProcessId(SafePipeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return PInvoke.GetNamedPipeServerProcessId(handle, out var pid) ? pid : null;
    }

    /// <summary>
    /// The canonical image path of a process, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is not "trusted". A peer whose path cannot be read is a peer that cannot be verified, and the
    /// caller must treat that as a refusal — the one interpretation that fails safe.
    /// </remarks>
    public static unsafe string? TryGetImagePath(uint processId)
    {
        var raw = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            bInheritHandle: false,
            processId);

        if (raw.IsNull)
        {
            return null;
        }

        using var handle = new SafeFileHandle(raw, ownsHandle: true);

        Span<char> buffer = stackalloc char[1024];
        var size = (uint)buffer.Length;

        if (!PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)
            || size == 0)
        {
            return null;
        }

        return PathCanonicalizer.Canonicalize(new string(buffer[..(int)size])).Path;
    }

    /// <summary>
    /// True when the peer's executable sits in the same directory as ours.
    /// </summary>
    /// <remarks>
    /// The App and the Agent ship in one Velopack package and publish into the same folder
    /// (docs/16_PACKAGING_AND_UPDATES.md §Package), so "same directory" is the whole test — and it survives
    /// an update, because the stable <c>current\</c> folder is what both live in.
    /// <para>
    /// Directory rather than exact path because the two peers are deliberately different executables.
    /// </para>
    /// </remarks>
    public static bool IsSameInstallDirectory(string? peerImagePath, string ownImagePath)
    {
        if (string.IsNullOrEmpty(peerImagePath) || string.IsNullOrEmpty(ownImagePath))
        {
            return false;
        }

        var peerDirectory = Path.GetDirectoryName(peerImagePath);
        var ownDirectory = Path.GetDirectoryName(PathCanonicalizer.Canonicalize(ownImagePath).Path);

        return !string.IsNullOrEmpty(peerDirectory)
            && !string.IsNullOrEmpty(ownDirectory)
            && string.Equals(peerDirectory, ownDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
