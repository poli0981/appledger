using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace AppLedger.Infrastructure.Platform;

/// <summary>
/// The known-folder roots the path policy is built from, resolved once through
/// <c>SHGetKnownFolderPath</c> rather than assumed (docs/11_SAFETY_POLICY.md §Path tiers: "never
/// hard-coded <c>C:\</c>").
/// </summary>
/// <remarks>
/// A folder that does not resolve on this machine comes back as null and is simply absent from the root
/// lists. That is deliberate: a missing <c>LocalAppDataLow</c> must not turn into an empty string, which
/// would compare as a prefix of every path and classify the whole disk as Tier 1.
/// </remarks>
public sealed class KnownFolders
{
    /// <summary>The folders of the process's own user. Resolved once, on first use.</summary>
    public static KnownFolders Current { get; } = new();

    private KnownFolders()
    {
        Windows = Resolve(PInvoke.FOLDERID_Windows);
        System32 = Resolve(PInvoke.FOLDERID_System);
        SystemX86 = Resolve(PInvoke.FOLDERID_SystemX86);
        ProgramFiles = Resolve(PInvoke.FOLDERID_ProgramFiles);
        ProgramFilesX86 = Resolve(PInvoke.FOLDERID_ProgramFilesX86);
        ProgramData = Resolve(PInvoke.FOLDERID_ProgramData);
        LocalAppData = Resolve(PInvoke.FOLDERID_LocalAppData);
        RoamingAppData = Resolve(PInvoke.FOLDERID_RoamingAppData);
        LocalAppDataLow = Resolve(PInvoke.FOLDERID_LocalAppDataLow);
        UserProfile = Resolve(PInvoke.FOLDERID_Profile);
        PublicUser = Resolve(PInvoke.FOLDERID_Public);
        Documents = Resolve(PInvoke.FOLDERID_Documents);
        SavedGames = Resolve(PInvoke.FOLDERID_SavedGames);
        UserProgramFiles = Resolve(PInvoke.FOLDERID_UserProgramFiles);

        ProtectedOsRoots = BuildProtectedOsRoots();
        SensitiveRoots = BuildSensitiveRoots();
    }

    /// <summary>The Windows directory. Covers System32, SysWOW64, WinSxS, servicing and the system Temp.</summary>
    public string? Windows { get; }

    /// <summary>The System32 directory.</summary>
    public string? System32 { get; }

    /// <summary>The SysWOW64 directory. Present even on ARM64, absent on some server SKUs.</summary>
    public string? SystemX86 { get; }

    /// <summary>The 64-bit Program Files directory.</summary>
    public string? ProgramFiles { get; }

    /// <summary>The 32-bit Program Files directory.</summary>
    public string? ProgramFilesX86 { get; }

    /// <summary>The machine-wide ProgramData directory.</summary>
    public string? ProgramData { get; }

    /// <summary>The user's local application data directory.</summary>
    public string? LocalAppData { get; }

    /// <summary>The user's roaming application data directory.</summary>
    public string? RoamingAppData { get; }

    /// <summary>The user's low-integrity application data directory.</summary>
    public string? LocalAppDataLow { get; }

    /// <summary>The user's profile directory.</summary>
    public string? UserProfile { get; }

    /// <summary>The Public profile directory.</summary>
    public string? PublicUser { get; }

    /// <summary>The user's Documents directory, which may be redirected to OneDrive or a network share.</summary>
    public string? Documents { get; }

    /// <summary>The user's Saved Games directory.</summary>
    public string? SavedGames { get; }

    /// <summary>The per-user Programs directory used by user-scope installers.</summary>
    public string? UserProgramFiles { get; }

    /// <summary>
    /// Tier-0 roots that come from known folders. The volume-relative ones ($Recycle.Bin and friends) are
    /// added by <see cref="Core.Policy.PathTierTable"/> because they exist on every drive.
    /// </summary>
    public IReadOnlyList<string> ProtectedOsRoots { get; }

    /// <summary>Tier-1 directories: credential stores, key material and token caches.</summary>
    public IReadOnlyList<string> SensitiveRoots { get; }

    /// <summary>
    /// The <c>%VAR%</c> values a catalog glob may reference (docs/13_CATALOG_RULES.md §Glob grammar).
    /// Built from the resolved folders rather than from the process environment, so a poisoned environment
    /// block cannot move a rule.
    /// </summary>
    public IReadOnlyDictionary<string, string> CatalogVariables
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(values, "LOCALAPPDATA", LocalAppData);
            Add(values, "APPDATA", RoamingAppData);
            Add(values, "USERPROFILE", UserProfile);
            Add(values, "PROGRAMDATA", ProgramData);
            Add(values, "PROGRAMFILES", ProgramFiles);
            Add(values, "PROGRAMFILES(X86)", ProgramFilesX86);
            Add(values, "PUBLIC", PublicUser);
            Add(values, "TEMP", Path.GetTempPath().TrimEnd('\\'));
            return values;
        }
    }

    private static void Add(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values[name] = value;
        }
    }

    private List<string> BuildProtectedOsRoots()
    {
        var roots = new List<string>(4);
        AddRoot(roots, Windows);

        // WindowsApps is Tier 0 in both Program Files views: an MSIX payload is never a pickable app root.
        AddRoot(roots, Combine(ProgramFiles, "WindowsApps"));
        AddRoot(roots, Combine(ProgramFilesX86, "WindowsApps"));
        return roots;
    }

    private List<string> BuildSensitiveRoots()
    {
        var roots = new List<string>(9);
        AddRoot(roots, Combine(LocalAppData, "Microsoft", "Credentials"));
        AddRoot(roots, Combine(RoamingAppData, "Microsoft", "Credentials"));
        AddRoot(roots, Combine(RoamingAppData, "Microsoft", "Protect"));
        AddRoot(roots, Combine(RoamingAppData, "Microsoft", "Crypto"));
        AddRoot(roots, Combine(LocalAppData, "Microsoft", "Vault"));
        AddRoot(roots, Combine(LocalAppData, "Microsoft", "TokenBroker"));
        AddRoot(roots, Combine(UserProfile, ".ssh"));
        AddRoot(roots, Combine(UserProfile, ".gnupg"));
        AddRoot(roots, Combine(RoamingAppData, "gnupg"));
        return roots;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (!string.IsNullOrEmpty(root))
        {
            roots.Add(root);
        }
    }

    private static string? Combine(string? root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);

    private static unsafe string? Resolve(in Guid folderId)
    {
        PWSTR buffer = default;
        try
        {
            // KF_FLAG_DEFAULT: return the current location without creating anything. We never create a
            // folder we did not make ourselves (docs/11 §Things the Agent explicitly does not do).
            var hr = PInvoke.SHGetKnownFolderPath(folderId, (uint)KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null, out buffer);
            if (hr.Failed || buffer.Value is null)
            {
                return null;
            }

            var path = buffer.ToString();
            return string.IsNullOrEmpty(path) ? null : path.TrimEnd('\\');
        }
        finally
        {
            if (buffer.Value is not null)
            {
                Marshal.FreeCoTaskMem((nint)buffer.Value);
            }
        }
    }
}
