using AppLedger.Core.Policy;

namespace AppLedger.Infrastructure.Storage;

/// <summary>
/// The only place in AppLedger that deletes anything. Every call is checked against a
/// <see cref="DataRoot"/> first, so a purge, an icon-cache sweep or a migration backup cleanup cannot
/// reach a user file even if it is handed a hostile path.
/// </summary>
/// <remarks>
/// <c>File.Delete</c> and <c>Directory.Delete</c> are banned repository-wide by
/// <c>BannedSymbols.txt</c>; this type carries the single sanctioned <c>RS0030</c> suppression that
/// docs/11_SAFETY_POLICY.md §Things the Agent explicitly does not do names by file. Suppressing
/// <c>RS0030</c> anywhere else is a review-blocking bug.
/// </remarks>
public sealed class DataRootFiles
{
    private readonly DataRoot _root;

    /// <summary>Creates a deleter bound to one data root.</summary>
    public DataRootFiles(DataRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
    }

    /// <summary>
    /// Deletes a file inside the data root. A path outside the root throws rather than being ignored: a
    /// caller that got here with a foreign path has a bug we want to see, not to swallow.
    /// </summary>
    public void DeleteFile(string path)
    {
        var canonical = RequireInsideRoot(path);

#pragma warning disable RS0030 // BannedSymbols.txt: this type is the sanctioned exception.
        File.Delete(canonical);
#pragma warning restore RS0030
    }

    /// <summary>Deletes a directory inside the data root.</summary>
    /// <param name="path">The directory to remove.</param>
    /// <param name="recursive">Whether to remove its contents too.</param>
    public void DeleteDirectory(string path, bool recursive)
    {
        var canonical = RequireInsideRoot(path);

        // Refusing the root itself keeps "purge everything" from removing the folder the Agent is about to
        // write its next log line into; purge empties the root, it does not delete it.
        if (PathRules.SamePath(canonical, _root.Root))
        {
            throw new ArgumentException("The data root itself is never deleted, only emptied.", nameof(path));
        }

#pragma warning disable RS0030 // BannedSymbols.txt: this type is the sanctioned exception.
        Directory.Delete(canonical, recursive);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Deletes a file if it exists, ignoring the case where it is already gone. Used by the purge paths,
    /// where a missing file is a success, not an error.
    /// </summary>
    public void DeleteFileIfExists(string path)
    {
        var canonical = RequireInsideRoot(path);
        if (File.Exists(canonical))
        {
            DeleteFile(canonical);
        }
    }

    private string RequireInsideRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!PathRules.TryNormalize(path, out var canonical, out var reason))
        {
            throw new ArgumentException($"'{path}' is not a usable path ({reason}).", nameof(path));
        }

        if (!_root.Contains(canonical))
        {
            throw new ArgumentException(
                "AppLedger deletes only inside its own data root (docs/11_SAFETY_POLICY.md).",
                nameof(path));
        }

        return canonical;
    }
}
