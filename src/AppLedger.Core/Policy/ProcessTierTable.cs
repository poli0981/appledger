namespace AppLedger.Core.Policy;

/// <summary>
/// Decides whether a process instance is <see cref="ProcessTier.ZeroTouch"/> from the two signals that
/// are available before any handle is opened: the image file name and the canonical image path
/// (docs/11_SAFETY_POLICY.md §Process access tiers).
/// </summary>
/// <remarks>
/// This is the half of the tier decision that is pure string work, so the whole table is testable on any
/// OS with no privileges (docs/19_TESTING.md §Layers). The dynamic half — an anti-cheat driver appearing
/// through ETW ImageLoad, or a service appearing through the SCM — promotes a whole *app* for the rest of
/// its lifetime and therefore belongs to the resolver, not to this table.
/// <para>
/// The order matters only for cost, not for correctness: every rule here answers "zero-touch", never
/// "safe to open". A path or name we do not recognise is <see cref="ProcessTier.Normal"/> because that is
/// what an unrecognised process is; the protection this table provides is a deny-list by design, exactly
/// as docs/11 describes it.
/// </para>
/// </remarks>
public sealed class ProcessTierTable
{
    /// <summary>
    /// The built-in protected-process list. These run as PPL (protected process light) and reject or
    /// strip handles; we never ask. The catalog may extend this list, never shrink it.
    /// </summary>
    public static IReadOnlySet<string> BuiltInProtectedProcesses { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lsass.exe",
            "csrss.exe",
            "wininit.exe",
            "winlogon.exe",
            "services.exe",
            "smss.exe",
            "MsMpEng.exe",
            "NisSrv.exe",
            "SecurityHealthService.exe",
            "MsSense.exe",
        };

    /// <summary>
    /// Anti-cheat helper executables that are Tier 2 wherever they run (docs/03_APP_IDENTITY.md
    /// §Host rules, <c>anticheat_helper</c>). They are matched by name because a game may ship them
    /// anywhere under its own root.
    /// </summary>
    public static IReadOnlySet<string> BuiltInAntiCheatExecutables { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "start_protected_game.exe",
            "EasyAntiCheat.exe",
            "EasyAntiCheat_EOS.exe",
            "EasyAntiCheat_EOS_Setup.exe",
            "BEService.exe",
            "BEService_x64.exe",
        };

    /// <summary>
    /// Directory names whose presence anywhere in an image path marks the process as anti-cheat protected
    /// (docs/11_SAFETY_POLICY.md §Process access tiers, "by game root containing"). The catalog extends
    /// this through <c>anticheat[].dirs</c>.
    /// </summary>
    public static IReadOnlyList<string> BuiltInAntiCheatDirectories { get; } =
    [
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "BattlEye",
        "GameGuard",
    ];

    private readonly HashSet<string> _protectedProcesses;
    private readonly HashSet<string> _antiCheatDirectories;

    /// <summary>Creates a table over the built-in lists plus whatever the catalog adds.</summary>
    /// <param name="additionalProtectedProcesses">Catalog <c>protected_processes[]</c>.</param>
    /// <param name="additionalAntiCheatDirectories">The union of every catalog <c>anticheat[].dirs</c>.</param>
    public ProcessTierTable(
        IReadOnlyList<string>? additionalProtectedProcesses = null,
        IReadOnlyList<string>? additionalAntiCheatDirectories = null)
    {
        _protectedProcesses = new HashSet<string>(BuiltInProtectedProcesses, StringComparer.OrdinalIgnoreCase);
        foreach (var name in BuiltInAntiCheatExecutables)
        {
            _protectedProcesses.Add(name);
        }

        if (additionalProtectedProcesses is not null)
        {
            foreach (var name in additionalProtectedProcesses)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _protectedProcesses.Add(name.Trim());
                }
            }
        }

        _antiCheatDirectories = new HashSet<string>(BuiltInAntiCheatDirectories, StringComparer.OrdinalIgnoreCase);
        if (additionalAntiCheatDirectories is not null)
        {
            foreach (var directory in additionalAntiCheatDirectories)
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    _antiCheatDirectories.Add(directory.Trim());
                }
            }
        }
    }

    /// <summary>
    /// The tier of a process instance. <see cref="ProcessTier.ZeroTouch"/> means no <c>OpenProcess</c> at
    /// all — not with reduced rights, not once.
    /// </summary>
    /// <param name="canonicalImagePath">
    /// The canonical image path when it is already known. It is legitimately null for a process we have
    /// never enriched, which is why <paramref name="imageFileName"/> alone must be able to decide.
    /// </param>
    /// <param name="imageFileName">The image file name without a path, e.g. <c>lsass.exe</c>.</param>
    public ProcessTier Classify(string? canonicalImagePath, string? imageFileName)
    {
        var name = imageFileName;
        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(canonicalImagePath))
        {
            name = PathRules.LeafName(canonicalImagePath);
        }

        if (!string.IsNullOrEmpty(name) && _protectedProcesses.Contains(name))
        {
            return ProcessTier.ZeroTouch;
        }

        return HasAntiCheatDirectory(canonicalImagePath) ? ProcessTier.ZeroTouch : ProcessTier.Normal;
    }

    /// <summary>
    /// True when any *whole* directory component of the path is an anti-cheat directory. Component
    /// equality rather than a substring test, so <c>D:\Games\BattlEyeFanClub</c> is not Tier 2.
    /// </summary>
    private bool HasAntiCheatDirectory(string? canonicalImagePath)
    {
        if (string.IsNullOrEmpty(canonicalImagePath))
        {
            return false;
        }

        // The final component is the executable, not a directory, so it is skipped: an exe called
        // BattlEye.exe is caught by the name list above if we mean to catch it at all.
        var segments = canonicalImagePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (_antiCheatDirectories.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }
}
