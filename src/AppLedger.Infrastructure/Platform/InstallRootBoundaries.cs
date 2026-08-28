using AppLedger.Core.Identity;

namespace AppLedger.Infrastructure.Platform;

/// <summary>
/// The directories an install root may not cross (docs/03_APP_IDENTITY.md §Install-root heuristic).
/// </summary>
/// <remarks>
/// This is a <i>policy</i> list, not a list of folders: it says where <see cref="InstallRootHeuristic"/> must
/// stop walking upwards, and it is the single thing standing between "one app per install" and "every system
/// binary resolves to one enormous app called Windows". Two lists that disagree do not fail — they produce
/// two different sets of app identities for the same machine, and the mismatch only shows up as history that
/// splits or merges when a different host writes it.
/// <para>
/// It lives here rather than in each host because there are three hosts: the Agent, the UI's Lite mode and
/// the S1 harness, and the harness only measures the shipping pipeline if it identifies apps the same way.
/// </para>
/// </remarks>
public static class InstallRootBoundaries
{
    /// <summary>Builds the boundary list for a set of known folders.</summary>
    public static IReadOnlyList<string> For(KnownFolders folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var boundaries = new List<string>();
        Add(folders.ProgramFiles);
        Add(folders.ProgramFilesX86);
        Add(folders.UserProgramFiles);
        Add(folders.ProgramData);
        Add(folders.LocalAppData);
        Add(folders.RoamingAppData);
        Add(folders.LocalAppDataLow);
        Add(folders.UserProfile);
        Add(folders.PublicUser);

        foreach (var protectedRoot in folders.ProtectedOsRoots)
        {
            Add(protectedRoot);
        }

        return boundaries;

        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && !boundaries.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                boundaries.Add(path);
            }
        }
    }
}
