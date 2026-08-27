using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Platform;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Platform;

/// <summary>
/// Smoke test for <c>QueryDosDeviceW</c>. ETW hands us image and file names in NT device form, and
/// <see cref="PathRules"/> deliberately refuses anything that is not drive-rooted — so without this
/// mapper every ETW path would be untierable (docs/11_SAFETY_POLICY.md §Canonicalization, step 1).
/// </summary>
public sealed class DevicePathMapperTests
{
    private readonly DevicePathMapper _mapper = new();

    private static string SystemDrive =>
        PathRules.VolumeRoot(KnownFolders.Current.Windows
            ?? throw new InvalidOperationException("FOLDERID_Windows did not resolve."))!;

    /// <summary>
    /// The round trip that matters: take a real DOS path, ask Windows for its device form through
    /// <c>GetFinalPathNameByHandle</c>'s sibling API, and map it back.
    /// </summary>
    [Fact]
    public void TryToDosPath_DeviceFormOfTheSystemDrive_MapsBackToTheDriveLetter()
    {
        var drive = SystemDrive.TrimEnd('\\');
        var device = QueryDevice(drive);
        device.ShouldNotBeNullOrEmpty();

        _mapper.TryToDosPath(device + @"\Windows\System32\notepad.exe", out var dosPath).ShouldBeTrue();

        dosPath.ShouldBe(drive + @"\Windows\System32\notepad.exe", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void TryToDosPath_DeviceRootWithNoRemainder_BecomesTheVolumeRoot()
    {
        var drive = SystemDrive.TrimEnd('\\');
        var device = QueryDevice(drive)!;

        _mapper.TryToDosPath(device, out var dosPath).ShouldBeTrue();

        dosPath.ShouldBe(drive + '\\', StringCompareShould.IgnoreCase);
    }

    /// <summary>
    /// The NT object-manager spelling of an ordinary path. It appears in scheduled-task actions and in
    /// some registry values, and is a DOS path wearing an NT hat.
    /// </summary>
    [Fact]
    public void TryToDosPath_NtObjectPrefix_IsStripped()
    {
        _mapper.TryToDosPath(@"\??\C:\Windows\notepad.exe", out var dosPath).ShouldBeTrue();

        dosPath.ShouldBe(@"C:\Windows\notepad.exe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"\Device\Mup\server\share\file.txt")]
    [InlineData(@"\Device\HarddiskVolume99999\x")]
    public void TryToDosPath_WhatWeCannotMap_IsRefusedRatherThanGuessed(string? ntPath)
    {
        _mapper.TryToDosPath(ntPath, out var dosPath).ShouldBeFalse();

        dosPath.ShouldBeNull();
    }

    /// <summary>
    /// Longest-prefix matching: <c>HarddiskVolume1</c> must not swallow a path that belongs to
    /// <c>HarddiskVolume10</c>. The component boundary check is what prevents it.
    /// </summary>
    [Fact]
    public void TryToDosPath_DoesNotMatchAPartialDeviceName()
    {
        var device = QueryDevice(SystemDrive.TrimEnd('\\'))!;

        _mapper.TryToDosPath(device + "9999" + @"\x", out var dosPath).ShouldBeFalse();

        dosPath.ShouldBeNull();
    }

    [Fact]
    public void Refresh_CanBeCalledRepeatedly_AndKeepsWorking()
    {
        _mapper.Refresh();
        _mapper.Refresh();

        var device = QueryDevice(SystemDrive.TrimEnd('\\'))!;
        _mapper.TryToDosPath(device + @"\Windows", out _).ShouldBeTrue();
    }

    /// <summary>
    /// Asks the same API the mapper uses, so the test does not hard-code a device name that differs
    /// between machines and between boots.
    /// </summary>
    private static string? QueryDevice(string driveWithColon)
    {
        var buffer = new char[2048];
        var length = Windows.Win32.PInvoke.QueryDosDevice(driveWithColon, buffer);
        if (length == 0)
        {
            return null;
        }

        var text = new string(buffer, 0, (int)length);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).TrimEnd('\\');
    }
}
