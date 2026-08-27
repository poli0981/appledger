using System.Diagnostics.CodeAnalysis;
using AppLedger.Core.Policy;
using Windows.Win32;

namespace AppLedger.Infrastructure.Platform;

/// <summary>
/// Translates NT device paths (<c>\Device\HarddiskVolume3\Games\x.exe</c>) into DOS paths
/// (<c>D:\Games\x.exe</c>) using <c>QueryDosDeviceW</c>.
/// </summary>
/// <remarks>
/// This is the one legitimate producer of the device paths that docs/11_SAFETY_POLICY.md
/// §Canonicalization step 1 rejects everywhere else. ETW hands us image and file names in device form
/// (<c>ProcessStart.ImageFileName</c>, FileIO events), and without this mapper none of them could be
/// tiered, because <see cref="PathRules"/> refuses a path that is not drive-rooted — deliberately, so a
/// device path can never reach the policy by accident.
/// <para>
/// The map is rebuilt on demand rather than watched: drive letters change when a volume is mounted or a
/// USB stick is pulled, but a miss is cheap and self-correcting, and polling the drive table would be a
/// standing cost for an event that happens a few times a day.
/// </para>
/// </remarks>
public sealed class DevicePathMapper
{
    private const string DevicePrefix = @"\Device\";
    private const string NtObjectPrefix = @"\??\";

    private readonly Lock _gate = new();
    private Dictionary<string, string> _deviceToDrive;

    /// <summary>Builds the mapper and takes a first snapshot of the drive table.</summary>
    public DevicePathMapper()
    {
        _deviceToDrive = BuildMap();
    }

    /// <summary>Re-reads the drive table. Called automatically when a lookup misses.</summary>
    public void Refresh()
    {
        var map = BuildMap();
        lock (_gate)
        {
            _deviceToDrive = map;
        }
    }

    /// <summary>
    /// Converts an NT path to its DOS form. Returns false for a device that has no drive letter (a volume
    /// mounted only into a directory, a network redirector), which the caller must then treat as a path we
    /// cannot tier rather than as a normal one.
    /// </summary>
    public bool TryToDosPath(string? ntPath, [NotNullWhen(true)] out string? dosPath)
    {
        dosPath = null;

        if (string.IsNullOrEmpty(ntPath))
        {
            return false;
        }

        // \??\C:\x is already a DOS path wearing an NT hat.
        if (ntPath.StartsWith(NtObjectPrefix, StringComparison.Ordinal))
        {
            dosPath = ntPath[NtObjectPrefix.Length..];
            return dosPath.Length > 0;
        }

        if (!ntPath.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryReplaceDevice(ntPath, out dosPath))
        {
            return true;
        }

        // A volume that appeared since the last snapshot: rebuild once, then give up for this path.
        Refresh();
        return TryReplaceDevice(ntPath, out dosPath);
    }

    private bool TryReplaceDevice(string ntPath, [NotNullWhen(true)] out string? dosPath)
    {
        Dictionary<string, string> map;
        lock (_gate)
        {
            map = _deviceToDrive;
        }

        // Longest match first: \Device\HarddiskVolume1 must not win over \Device\HarddiskVolume10.
        foreach (var (device, drive) in map)
        {
            if (ntPath.Length == device.Length && ntPath.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                dosPath = drive + '\\';
                return true;
            }

            if (ntPath.Length > device.Length
                && ntPath[device.Length] == '\\'
                && ntPath.AsSpan(0, device.Length).Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                dosPath = drive + ntPath[device.Length..];
                return true;
            }
        }

        dosPath = null;
        return false;
    }

    private static Dictionary<string, string> BuildMap()
    {
        // Ordered longest device name first so the prefix scan cannot match a shorter sibling.
        var pairs = new List<KeyValuePair<string, string>>(26);
        Span<char> name = stackalloc char[3];
        name[1] = ':';
        name[2] = '\0';

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            name[0] = letter;
            var target = QueryDevice(new string(name[..2]));
            if (!string.IsNullOrEmpty(target))
            {
                pairs.Add(new KeyValuePair<string, string>(target, new string([letter, ':'])));
            }
        }

        pairs.Sort(static (a, b) => b.Key.Length.CompareTo(a.Key.Length));

        var map = new Dictionary<string, string>(pairs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (device, drive) in pairs)
        {
            map[device] = drive;
        }

        return map;
    }

    private static string? QueryDevice(string driveWithColon)
    {
        // A device target is a handful of characters (\Device\HarddiskVolume3), and CsWin32 does not mark
        // QueryDosDeviceW with SetLastError, so there is no reliable way to tell "buffer too small" from
        // "no such drive letter". One generous buffer and a zero-means-absent rule is both simpler and
        // more honest than a retry loop reading a last error that may not have been captured.
        var buffer = new char[2048];

        var length = PInvoke.QueryDosDevice(driveWithColon, buffer);
        if (length == 0)
        {
            return null;
        }

        // The result is a null-separated stack of targets; the first one is the current mapping.
        var text = new string(buffer, 0, (int)length);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        var target = (end < 0 ? text : text[..end]).TrimEnd('\\');
        return target.Length == 0 ? null : target;
    }
}
