using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;

namespace AppLedger.Infrastructure.Identity;

/// <summary>
/// Reads the package-manager layouts step 7 of docs/03_APP_IDENTITY.md consults: one directory per package,
/// and the directory name is the package name.
/// </summary>
/// <remarks>
/// Both managers are read from their directory layout rather than by asking the tool, because shelling out
/// to <c>scoop</c> or <c>choco</c> would mean starting a process to learn something the file system already
/// says — and AppLedger starts as few processes as it can (docs/11_SAFETY_POLICY.md).
/// <para>
/// Scoop's <c>apps\&lt;name&gt;\current</c> is a junction to the installed version. It is left alone here:
/// the claim is <c>apps\&lt;name&gt;</c>, which contains <c>current</c> and every retained version, so an
/// image under any of them resolves to the same package. Following the link would also mean a reparse-point
/// traversal, which path policy treats as its own question.
/// </para>
/// </remarks>
public sealed class PackageManagerReader
{
    private readonly IPolicyGuard _policy;
    private readonly KnownFolders _folders;

    /// <summary>Creates a reader over the current user's folders.</summary>
    public PackageManagerReader(IPolicyGuard policy, KnownFolders folders)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(folders);
        _policy = policy;
        _folders = folders;
    }

    /// <summary>
    /// Scoop packages. Both the per-user root and a machine-wide <c>SCOOP_GLOBAL</c> install are read, and
    /// <c>SCOOP</c> is honoured because a relocated root is common enough that assuming the default would
    /// quietly index nothing.
    /// </summary>
    public IReadOnlyList<LauncherIndexEntry> ReadScoop()
    {
        var entries = new List<LauncherIndexEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ScoopRoots())
        {
            foreach (var directory in Directories(Path.Combine(root, "apps")))
            {
                var name = Path.GetFileName(directory);
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    Add(entries, name, directory);
                }
            }
        }

        return entries;
    }

    /// <summary>Chocolatey packages, from <c>%ProgramData%\chocolatey\lib</c>.</summary>
    public IReadOnlyList<LauncherIndexEntry> ReadChocolatey()
    {
        var entries = new List<LauncherIndexEntry>();

        if (ChocolateyRoot() is not { } root)
        {
            return entries;
        }

        foreach (var directory in Directories(Path.Combine(root, "lib")))
        {
            var name = Path.GetFileName(directory);
            if (!string.IsNullOrEmpty(name))
            {
                Add(entries, name, directory);
            }
        }

        return entries;
    }

    private List<string> ScoopRoots()
    {
        var roots = new List<string>();

        if (Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } user)
        {
            roots.Add(user);
        }
        else if (_folders.UserProfile is { } profile)
        {
            roots.Add(Path.Combine(profile, "scoop"));
        }

        if (Environment.GetEnvironmentVariable("SCOOP_GLOBAL") is { Length: > 0 } global)
        {
            roots.Add(global);
        }
        else if (_folders.ProgramData is { } programData)
        {
            roots.Add(Path.Combine(programData, "scoop"));
        }

        return roots;
    }

    private string? ChocolateyRoot() =>
        Environment.GetEnvironmentVariable("ChocolateyInstall") is { Length: > 0 } configured
            ? configured
            : _folders.ProgramData is { } programData ? Path.Combine(programData, "chocolatey") : null;

    private void Add(List<LauncherIndexEntry> entries, string name, string location)
    {
        var decision = _policy.Evaluate(location);

        if (decision is { Allowed: true, Canonical: not null } && decision.Tier != PathTier.ProtectedOs)
        {
            entries.Add(new LauncherIndexEntry(name, decision.Canonical));
        }
    }

    private static IEnumerable<string> Directories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException)
        {
            return [];
        }
    }
}
