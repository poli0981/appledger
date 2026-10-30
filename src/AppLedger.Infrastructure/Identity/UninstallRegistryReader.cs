using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using Microsoft.Win32;

namespace AppLedger.Infrastructure.Identity;

/// <summary>
/// Reads the Uninstall keys into the index step 6 of docs/03_APP_IDENTITY.md consults.
/// </summary>
/// <remarks>
/// Read-only, and that is a product rule rather than a style preference: AppLedger never writes the registry
/// (docs/11_SAFETY_POLICY.md, docs/23_NON_GOALS.md). Only <see cref="RegistryKey.OpenSubKey(string)"/> is
/// used, which opens for read.
/// <para>
/// Three views, because an installer may have written to any of them: the 64-bit and 32-bit HKLM views are
/// genuinely different keys rather than two names for one, and a per-user install appears only under HKCU.
/// An app installed both machine-wide and per-user yields two entries with different key names, which is
/// correct — they are two installations, and the one whose <c>InstallLocation</c> contains the running image
/// is the one that claims it.
/// </para>
/// </remarks>
public sealed class UninstallRegistryReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Views =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    private readonly IPolicyGuard _policy;

    /// <summary>Creates a reader whose paths are tiered by the given guard.</summary>
    public UninstallRegistryReader(IPolicyGuard policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>
    /// Every entry that could claim an image. An entry with neither an <c>InstallLocation</c> nor a
    /// <c>DisplayIcon</c> is skipped: it has no path, so it can never match anything, and keeping it would
    /// only grow a snapshot that lives in the Agent's working set for the whole run.
    /// </summary>
    public IReadOnlyList<UninstallIndexEntry> Read()
    {
        var entries = new List<UninstallIndexEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in Views)
        {
            ReadView(hive, view, entries, seen);
        }

        return entries;
    }

    private void ReadView(
        RegistryHive hive, RegistryView view, List<UninstallIndexEntry> entries, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);

            if (uninstall is null)
            {
                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                ReadEntry(uninstall, name, entries, seen);
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // A hive we cannot open is a smaller index, not a broken Agent: identity falls back a step and
            // says so in its confidence. The same reasoning PolicyGuard uses for a rejected catalog.
        }
    }

    private void ReadEntry(
        RegistryKey uninstall, string name, List<UninstallIndexEntry> entries, HashSet<string> seen)
    {
        try
        {
            using var key = uninstall.OpenSubKey(name);
            if (key is null)
            {
                return;
            }

            // SystemComponent is how Windows and its redistributables ask not to be listed as applications.
            // Honouring it keeps the index to things a user would call an app.
            if (key.GetValue("SystemComponent") is int component && component != 0)
            {
                return;
            }

            var installLocation = Canonical(key.GetValue("InstallLocation") as string);
            var displayIcon = Canonical(StripIconIndex(key.GetValue("DisplayIcon") as string));

            if (installLocation is null && displayIcon is null)
            {
                return;
            }

            // The key name is the id (docs/03 §app_id scheme), so a duplicate key name across views would
            // mint one id twice. First view wins, and the order above puts machine-wide before per-user.
            if (!seen.Add(name))
            {
                return;
            }

            entries.Add(new UninstallIndexEntry(
                name,
                (key.GetValue("DisplayName") as string)?.Trim(),
                installLocation,
                displayIcon,
                (key.GetValue("Publisher") as string)?.Trim()));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // One unreadable entry, not a reason to abandon the rest of the hive.
        }
    }

    /// <summary>
    /// A path through <see cref="IPolicyGuard"/>, or null when it is unusable or Tier 0. Tier-0 entries are
    /// dropped on purpose: something installed under a protected OS root is a Windows component, and
    /// docs/03 §Windows components resolves those to the <c>sys:*</c> family rather than to an
    /// <c>uninst:</c> id.
    /// </summary>
    private string? Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var decision = _policy.Evaluate(raw.Trim().Trim('"'));

        return decision is { Allowed: true, Canonical: not null } && decision.Tier != PathTier.ProtectedOs
            ? decision.Canonical
            : null;
    }

    /// <summary>
    /// <c>DisplayIcon</c> usually names the main executable, but it carries an icon index often enough to
    /// matter: <c>"C:\...\app.exe",0</c>. The comma is cut back only past a drive letter, so <c>C:</c> in a
    /// path with no index is never mistaken for one.
    /// </summary>
    private static string? StripIconIndex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().Trim('"');
        var comma = value.LastIndexOf(',');

        return comma > 2 ? value[..comma].Trim().Trim('"') : value;
    }

    /// <summary>
    /// The failures a registry read legitimately produces on someone else's machine: a key removed between
    /// the enumeration and the open, a permission we do not have, a hive that is corrupt. Anything else is
    /// a bug and is left to propagate.
    /// </summary>
    private static bool IsRegistryFailure(Exception ex) =>
        ex is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException;
}
