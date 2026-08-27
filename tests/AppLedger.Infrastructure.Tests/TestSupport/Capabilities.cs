using AppLedger.Core.Policy;
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
internal static class Capabilities
{
    private static readonly Lazy<bool> JunctionsLazy = new(ProbeJunctions);
    private static readonly Lazy<bool> ShortNamesLazy = new(ProbeShortNames);

    /// <summary>True when this account can create a directory junction in the temp directory.</summary>
    internal static bool CanCreateJunctions => JunctionsLazy.Value;

    /// <summary>True when the system volume still generates 8.3 short names.</summary>
    internal static bool HasShortNames => ShortNamesLazy.Value;

    /// <summary>The <c>WINDOW~1</c> form of the Windows directory, when short names exist.</summary>
    internal static string? ShortWindowsRoot
    {
        get
        {
            var windows = KnownFolders.Current.Windows;
            var volume = windows is null ? null : PathRules.VolumeRoot(windows);
            return volume is null ? null : Path.Combine(volume, "WINDOW~1");
        }
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

    private static bool ProbeShortNames() => ShortWindowsRoot is { } root && Directory.Exists(root);
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

/// <summary>A fact that only runs where the system volume generates 8.3 short names.</summary>
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
