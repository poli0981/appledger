using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AppLedger.Infrastructure.Platform;
using Xunit;

namespace AppLedger.Infrastructure.Tests.TestSupport;

/// <summary>
/// File-system capabilities the canonicalization tests need but cannot assume.
/// </summary>
/// <remarks>
/// xUnit 2.9 has no dynamic skip (<c>Assert.Skip</c> arrived in v3), so a capability that a machine may
/// legitimately lack is probed once here and turned into a static <c>Skip</c> reason by the attributes
/// below. The point is that a locked-down machine and a broken policy must not produce the same CI
/// result: one is a skip, the other is a failure.
/// </remarks>
internal static partial class Capabilities
{
    private static readonly Lazy<bool> JunctionsLazy = new(ProbeJunctions);
    private static readonly Lazy<string?> ShortNamedWindowsDirectoryLazy = new(ProbeShortNamedWindowsDirectory);

    /// <summary>True when this account can create a directory junction in the temp directory.</summary>
    internal static bool CanCreateJunctions => JunctionsLazy.Value;

    /// <summary>
    /// A directory under the Windows root whose 8.3 short name really differs from its long name, or null
    /// when this volume no longer generates short names.
    /// </summary>
    /// <remarks>
    /// It is discovered, never guessed. docs/11_SAFETY_POLICY.md §Tests named
    /// <c>C:\WINDOW~1\SYSTEM~1</c>, but neither component is real: <c>Windows</c> and <c>System32</c> are
    /// both eight characters or fewer and therefore already 8.3-legal, so neither has a <c>~1</c> form.
    /// Whatever <c>WINDOW~1</c> resolves to on a given machine is some other directory entirely.
    /// </remarks>
    internal static string? ShortNamedWindowsDirectory => ShortNamedWindowsDirectoryLazy.Value;

    /// <summary>True when a short-named directory was found to test expansion against.</summary>
    internal static bool HasShortNames => ShortNamedWindowsDirectory is not null;

    /// <summary>
    /// <c>GetShortPathNameW</c>, declared here rather than in <c>NativeMethods.txt</c>: AppLedger never
    /// needs the short form of a path in production, and adding a production P/Invoke to build a test
    /// fixture would be the wrong trade. Constructing a Windows-specific fixture is the one thing a test
    /// legitimately does that the product does not.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, Span<char> shortPath, uint bufferLength);

    /// <summary>The 8.3 form of a path, or null when the volume has no short name for it.</summary>
    internal static bool TryGetShortPath(string longPath, [NotNullWhen(true)] out string? shortPath)
    {
        Span<char> buffer = stackalloc char[512];
        var length = GetShortPathName(longPath, buffer, (uint)buffer.Length);

        if (length == 0 || length >= buffer.Length)
        {
            shortPath = null;
            return false;
        }

        var result = new string(buffer[..(int)length]);

        // A path that is already 8.3-legal comes back unchanged, which is not a short name to test with.
        shortPath = result;
        return !string.Equals(result, longPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProbeJunctions()
    {
        var link = Path.Combine(Path.GetTempPath(), "appledger-probe-" + Guid.NewGuid().ToString("N")[..12]);
        var target = KnownFolders.Current.System32;
        if (target is null || !Junctions.TryCreate(link, target))
        {
            return false;
        }

        try
        {
            // A non-recursive delete on a reparse point removes the link, never the target.
            Directory.Delete(link);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return true;
    }

    /// <summary>
    /// Finds a real short-named directory inside the Windows root, so the expansion test can assert a
    /// Tier-0 outcome without inventing a path. Only names longer than eight characters can have one.
    /// </summary>
    private static string? ProbeShortNamedWindowsDirectory()
    {
        var windows = KnownFolders.Current.Windows;
        if (windows is null)
        {
            return null;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(windows))
            {
                var name = Path.GetFileName(directory);
                if (name.Length <= 8 || name.Contains(' ', StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryGetShortPath(directory, out _))
                {
                    return directory;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}

/// <summary>A fact that only runs where a directory junction can be created.</summary>
internal sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (!Capabilities.CanCreateJunctions)
        {
            Skip = "This machine cannot create directory junctions, so the reparse-point policy is untested here.";
        }
    }
}

/// <summary>A fact that only runs where the system volume still generates 8.3 short names.</summary>
internal sealed class ShortNameFactAttribute : FactAttribute
{
    public ShortNameFactAttribute()
    {
        if (!Capabilities.HasShortNames)
        {
            Skip = "8.3 name generation is disabled on this volume, so short-name expansion is untested here.";
        }
    }
}
