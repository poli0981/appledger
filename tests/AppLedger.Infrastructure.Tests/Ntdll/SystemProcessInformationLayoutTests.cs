using System.Runtime.InteropServices;
using AppLedger.Infrastructure.Ntdll;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Ntdll;

/// <summary>
/// The struct is hand-written (docs/17_BUILD.md §CsWin32), so nothing but this test stands between a
/// mistyped field and every counter in the product being read from the wrong offset — silently, with
/// plausible-looking numbers.
/// </summary>
/// <remarks>
/// The expected offsets are the same on x64 and ARM64: both are LP64 with 8-byte alignment. Running this
/// on both platforms is what turns that sentence into a fact, which is why CI builds ARM64 too.
/// </remarks>
public sealed class SystemProcessInformationLayoutTests
{
    public static TheoryData<string, int> ExpectedOffsets() => new()
    {
        { nameof(SystemProcessInformation.NextEntryOffset), 0x00 },
        { nameof(SystemProcessInformation.NumberOfThreads), 0x04 },
        { nameof(SystemProcessInformation.WorkingSetPrivateSize), 0x08 },
        { nameof(SystemProcessInformation.HardFaultCount), 0x10 },
        { nameof(SystemProcessInformation.NumberOfThreadsHighWatermark), 0x14 },
        { nameof(SystemProcessInformation.CycleTime), 0x18 },
        { nameof(SystemProcessInformation.CreateTime), 0x20 },
        { nameof(SystemProcessInformation.UserTime), 0x28 },
        { nameof(SystemProcessInformation.KernelTime), 0x30 },
        { nameof(SystemProcessInformation.ImageName), 0x38 },
        { nameof(SystemProcessInformation.BasePriority), 0x48 },
        { nameof(SystemProcessInformation.UniqueProcessId), 0x50 },
        { nameof(SystemProcessInformation.InheritedFromUniqueProcessId), 0x58 },
        { nameof(SystemProcessInformation.HandleCount), 0x60 },
        { nameof(SystemProcessInformation.SessionId), 0x64 },
        { nameof(SystemProcessInformation.UniqueProcessKey), 0x68 },
        { nameof(SystemProcessInformation.PeakVirtualSize), 0x70 },
        { nameof(SystemProcessInformation.VirtualSize), 0x78 },
        { nameof(SystemProcessInformation.PageFaultCount), 0x80 },
        { nameof(SystemProcessInformation.PeakWorkingSetSize), 0x88 },
        { nameof(SystemProcessInformation.WorkingSetSize), 0x90 },
        { nameof(SystemProcessInformation.QuotaPeakPagedPoolUsage), 0x98 },
        { nameof(SystemProcessInformation.QuotaPagedPoolUsage), 0xA0 },
        { nameof(SystemProcessInformation.QuotaPeakNonPagedPoolUsage), 0xA8 },
        { nameof(SystemProcessInformation.QuotaNonPagedPoolUsage), 0xB0 },
        { nameof(SystemProcessInformation.PagefileUsage), 0xB8 },
        { nameof(SystemProcessInformation.PeakPagefileUsage), 0xC0 },
        { nameof(SystemProcessInformation.PrivatePageCount), 0xC8 },
        { nameof(SystemProcessInformation.ReadOperationCount), 0xD0 },
        { nameof(SystemProcessInformation.WriteOperationCount), 0xD8 },
        { nameof(SystemProcessInformation.OtherOperationCount), 0xE0 },
        { nameof(SystemProcessInformation.ReadTransferCount), 0xE8 },
        { nameof(SystemProcessInformation.WriteTransferCount), 0xF0 },
        { nameof(SystemProcessInformation.OtherTransferCount), 0xF8 },
    };

    [Theory]
    [MemberData(nameof(ExpectedOffsets))]
    public void SystemProcessInformation_FieldOffset_MatchesTheNativeLayout(string fieldName, int expected) =>
        Marshal.OffsetOf<SystemProcessInformation>(fieldName).ToInt32().ShouldBe(expected, fieldName);

    [Fact]
    public void SystemProcessInformation_Size_IsTwoHundredAndFiftySixBytes() =>
        Marshal.SizeOf<SystemProcessInformation>().ShouldBe(0x100);

    [Fact]
    public void UnicodeString_Layout_PutsTheBufferAfterTheAlignmentPadding()
    {
        Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.Length)).ToInt32().ShouldBe(0);
        Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.MaximumLength)).ToInt32().ShouldBe(2);
        Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.Buffer)).ToInt32().ShouldBe(8);
        Marshal.SizeOf<UnicodeString>().ShouldBe(16);
    }

    /// <summary>
    /// Every field is accounted for. Adding one without giving it an expected offset would otherwise slip
    /// through, and a field inserted in the middle shifts everything after it.
    /// </summary>
    [Fact]
    public void SystemProcessInformation_EveryFieldHasAnAssertedOffset()
    {
        var declared = typeof(SystemProcessInformation)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.Name);

        var asserted = ExpectedOffsets().Select(row => (string)row[0]!);

        declared.Except(asserted).ShouldBeEmpty("every field must have an asserted offset");
    }
}
