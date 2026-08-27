using AppLedger.Core.Policy;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace AppLedger.Infrastructure.Platform;

/// <summary>The outcome of resolving a lexically normalized path against the live file system.</summary>
/// <param name="Path">The canonical path. Never null: on failure this is the lexical form we started with.</param>
/// <param name="Unresolved">
/// True when reparse points could not be collapsed — typically access denied, or the path does not exist
/// yet. The caller must then treat the result as Tier 0 if its lexical form is already under a Tier-0
/// root, and Tier 3 with a warning otherwise (docs/11_SAFETY_POLICY.md §Canonicalization, step 3).
/// </param>
public readonly record struct CanonicalPath(string Path, bool Unresolved);

/// <summary>
/// Steps 2b and 3 of docs/11_SAFETY_POLICY.md §Canonicalization: expand 8.3 short names, then collapse
/// every junction, symlink and mount point by asking the file system what the path really is.
/// </summary>
/// <remarks>
/// The lexical steps live in <see cref="PathRules"/> in Core; only the two that need the file system are
/// here. Opening the final path once with <c>FILE_FLAG_BACKUP_SEMANTICS</c> and calling
/// <c>GetFinalPathNameByHandleW</c> collapses reparse points anywhere in the path, which is why docs/11
/// prefers it over walking ancestors one at a time.
/// </remarks>
public static class PathCanonicalizer
{
    /// <summary>
    /// The only access right we ever ask a file for. It permits reading attributes and nothing else — not
    /// the contents, not the security descriptor (docs/11 §Principle: observer, not intruder).
    /// </summary>
    private const uint FileReadAttributes = 0x0080;

    // Both APIs return the required size when the buffer is too small, so one retry is always enough.
    // 512 covers essentially every real path in the first call.
    private const int InitialBufferChars = 512;

    private const FILE_SHARE_MODE ShareEverything =
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    private const GETFINALPATHNAMEBYHANDLE_FLAGS FinalPathFlags =
        GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS;

    /// <summary>
    /// Resolves an already lexically normalized, drive-rooted path. Never throws: an unusable path comes
    /// back unchanged with <see cref="CanonicalPath.Unresolved"/> set.
    /// </summary>
    public static CanonicalPath Canonicalize(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);

        var expanded = ExpandShortName(normalizedPath);
        var final = ResolveFinalPath(expanded);

        if (final is null)
        {
            return new CanonicalPath(expanded, Unresolved: true);
        }

        // The final path comes back extended-length prefixed, and a volume with no drive letter comes back
        // as a volume GUID path. Running it back through the lexical rules strips the prefix and rejects
        // the GUID form, which we report as unresolved rather than inventing a drive letter for it.
        return PathRules.TryNormalize(final, out var normalizedFinal, out _)
            ? new CanonicalPath(normalizedFinal, Unresolved: false)
            : new CanonicalPath(expanded, Unresolved: true);
    }

    /// <summary>
    /// Expands 8.3 components (<c>PROGRA~1</c>) to their long form. A path that does not exist cannot be
    /// expanded and is returned as given — the tier comparison then runs on the lexical form, which is
    /// exactly what docs/11 step 3 calls the lexical fallback.
    /// </summary>
    private static string ExpandShortName(string path) =>
        CallWithGrowingBuffer(path, static (input, buffer) => PInvoke.GetLongPathName(input, buffer)) ?? path;

    /// <summary>
    /// Opens the path for attribute reading only and asks Windows for its final name. Returns null when
    /// the path cannot be opened at all, which is the normal case for a Tier-0 file we are not allowed to
    /// touch and for a path that does not exist.
    /// </summary>
    private static string? ResolveFinalPath(string path)
    {
        using var handle = OpenForAttributes(path);
        if (handle is null || handle.IsInvalid)
        {
            return null;
        }

        return CallWithGrowingBuffer(handle, static (h, buffer) => PInvoke.GetFinalPathNameByHandle(h, buffer, FinalPathFlags));
    }

    /// <summary>
    /// Runs a Win32 "fill this buffer, or tell me how big it should have been" call, growing once. Shared
    /// so the off-by-one around the terminating null is written in exactly one place.
    /// </summary>
    private static string? CallWithGrowingBuffer<TInput>(TInput input, Func<TInput, char[], uint> call)
    {
        var buffer = new char[InitialBufferChars];

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = call(input, buffer);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > PathRules.MaxPathLength)
            {
                return null;
            }

            // The return value here is the required size *including* the terminator.
            buffer = new char[length];
        }

        return null;
    }

    /// <summary>
    /// Opens a file *or directory* with no data access at all. <c>FILE_FLAG_BACKUP_SEMANTICS</c> is what
    /// makes a directory openable and is the reason <c>CreateFile</c> is in <c>NativeMethods.txt</c>: no
    /// BCL API exposes it. The share mode is fully permissive so we never block anyone else's access to
    /// their own file.
    /// </summary>
    private static SafeFileHandle? OpenForAttributes(string path)
    {
        try
        {
            var handle = PInvoke.CreateFile(
                path,
                FileReadAttributes,
                ShareEverything,
                lpSecurityAttributes: null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
                hTemplateFile: null);

            return handle.IsInvalid ? null : handle;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
