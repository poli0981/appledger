using System.IO.Pipes;
using System.Security.Principal;
using AppLedger.Infrastructure.Ipc;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Ipc;

/// <summary>
/// The descriptor the Agent's pipe is created with, and the P/Invoke route that is the only way to apply it
/// (docs/24_ADR.md ADR-17 and the Finding of 2026-08-28).
/// </summary>
/// <remarks>
/// Most of this runs unelevated, which was itself the discovery: writing a mandatory label at or below your
/// own integrity needs no privilege, so the descriptor can be proven on CI. What CI cannot prove is the
/// cross-integrity connect — see <c>PipeSecurityAdminTests</c>.
/// </remarks>
public sealed class NamedPipeServerFactoryTests
{
    private static string UniqueName() => $"AppLedger.test.{Guid.NewGuid():N}";

    [Fact]
    public void SddlForCurrentUser_GrantsTheUserAndAdministratorsAndNobodyElse()
    {
        var user = new SecurityIdentifier(WellKnownSidType.NullSid, null);

        var sddl = NamedPipeServerFactory.SddlForCurrentUser(user);

        sddl.ShouldStartWith($"D:(A;;FA;;;{user.Value})(A;;FA;;;BA)");
        sddl.ShouldNotContain("WD");   // Everyone
        sddl.ShouldNotContain("AN");   // Anonymous
    }

    /// <summary>
    /// The piece <c>PipeOptions.CurrentUserOnly</c> cannot express at all. Without it an object created by
    /// the High-integrity Agent denies the Medium-integrity UI the write access that connecting requires,
    /// and no DACL can fix that.
    /// </summary>
    [Fact]
    public void SddlForCurrentUser_CarriesAMediumMandatoryLabelWithNoWriteUp() =>
        NamedPipeServerFactory.SddlForCurrentUser(new SecurityIdentifier(WellKnownSidType.NullSid, null))
            .ShouldContain("S:(ML;;NW;;;ME)");

    /// <summary>
    /// The assertion this whole class exists for. The managed <c>PipeSecurity</c> route accepts the same
    /// SDDL, silently drops the label, and produces a pipe that an unelevated UI cannot connect to — with
    /// nothing thrown and nothing logged. Asking the created pipe what it actually carries is the only way
    /// to tell the two outcomes apart.
    /// </summary>
    [Fact]
    public void Create_AppliesTheMandatoryLabelToTheRealPipe()
    {
        using var server = NamedPipeServerFactory.Create(UniqueName(), maxInstances: 4);

        var applied = NamedPipeServerFactory.ReadAppliedSddl(server.SafePipeHandle);

        applied.ShouldNotBeNull();
        applied.ShouldContain("(ML;;NW;;;ME)");
    }

    [Fact]
    public void Create_AppliesTheDaclToTheRealPipe()
    {
        using var server = NamedPipeServerFactory.Create(UniqueName(), maxInstances: 4);

        var applied = NamedPipeServerFactory.ReadAppliedSddl(server.SafePipeHandle);

        applied.ShouldNotBeNull();
        applied.ShouldContain(WindowsIdentity.GetCurrent().User!.Value);
        applied.ShouldContain("BA");
    }

    /// <summary>The DACL must not lock out the process that created the pipe.</summary>
    [Fact]
    public async Task Create_StillAcceptsAConnectionFromThisUser()
    {
        var name = UniqueName();
        using var server = NamedPipeServerFactory.Create(name, maxInstances: 4);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(timeout: 5_000);
        await accepting;

        server.IsConnected.ShouldBeTrue();
    }

    /// <summary>
    /// A second instance of the same name has to be creatable, or the server can only ever serve one client
    /// however high <c>maxInstances</c> is.
    /// </summary>
    [Fact]
    public void Create_SecondInstanceOfTheSameName_Succeeds()
    {
        var name = UniqueName();

        using var first = NamedPipeServerFactory.Create(name, maxInstances: 4);
        using var second = NamedPipeServerFactory.Create(name, maxInstances: 4);

        second.ShouldNotBeNull();
    }

    [Fact]
    public void Create_BeyondMaxInstances_Fails()
    {
        var name = UniqueName();
        using var only = NamedPipeServerFactory.Create(name, maxInstances: 1);

        Should.Throw<IOException>(() => NamedPipeServerFactory.Create(name, maxInstances: 1));
    }

    [Fact]
    public void Create_MalformedSddl_FailsLoudlyRatherThanSilentlyDroppingIt() =>
        Should.Throw<InvalidOperationException>(
            () => NamedPipeServerFactory.Create(UniqueName(), maxInstances: 1, sddl: "not an sddl string"));

    // -- peer verification -------------------------------------------------------------------------------

    /// <summary>
    /// The anti-squatting defence, over a real pipe: each side resolves the other's PID and canonical image
    /// path. Both ends are this test process, so the two must agree.
    /// </summary>
    [Fact]
    public async Task PeerVerification_OverARealPipe_ResolvesBothEndsToThisProcess()
    {
        var name = UniqueName();
        using var server = NamedPipeServerFactory.Create(name, maxInstances: 4);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(timeout: 5_000);
        await accepting;

        var clientPid = PipePeer.ClientProcessId(server.SafePipeHandle);
        var serverPid = PipePeer.ServerProcessId(client.SafePipeHandle);

        clientPid.ShouldBe((uint)Environment.ProcessId);
        serverPid.ShouldBe((uint)Environment.ProcessId);

        var ownPath = PipePeer.TryGetImagePath((uint)Environment.ProcessId)!;
        PipePeer.IsSameInstallDirectory(PipePeer.TryGetImagePath(clientPid!.Value), ownPath).ShouldBeTrue();
    }

    /// <summary>
    /// The App and the Agent ship in one package and publish into the same folder, so "same directory" is
    /// the whole test — and it survives an update, because both live in the stable `current\` folder.
    /// </summary>
    [Fact]
    public void IsSameInstallDirectory_PeerBesideUs_IsAccepted() =>
        PipePeer.IsSameInstallDirectory(
            @"C:\Users\x\AppData\Local\AppLedger\current\AppLedger.Agent.exe",
            @"C:\Users\x\AppData\Local\AppLedger\current\AppLedger.exe")
            .ShouldBeTrue();

    [Fact]
    public void IsSameInstallDirectory_PeerElsewhere_IsRefused() =>
        PipePeer.IsSameInstallDirectory(
            @"C:\Users\x\Downloads\AppLedger.Agent.exe",
            @"C:\Users\x\AppData\Local\AppLedger\current\AppLedger.exe")
            .ShouldBeFalse();

    /// <summary>
    /// A peer whose path could not be read is a peer that could not be verified, and the only reading that
    /// fails safe is refusal. Returning "trusted" for the unknown case is how a check like this becomes
    /// decorative.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSameInstallDirectory_UnreadablePeer_IsRefused(string? peer) =>
        PipePeer.IsSameInstallDirectory(peer, @"C:\Users\x\AppData\Local\AppLedger\current\AppLedger.exe")
            .ShouldBeFalse();

    [Fact]
    public void TryGetImagePath_OwnProcess_ReturnsThisExecutable() =>
        PipePeer.TryGetImagePath((uint)Environment.ProcessId).ShouldNotBeNullOrEmpty();

    [Fact]
    public void TryGetImagePath_ProcessThatDoesNotExist_IsNull() =>
        PipePeer.TryGetImagePath(0xFFFF_FFF0).ShouldBeNull();
}

/// <summary>
/// The half only an elevated box can answer: that a <b>High-integrity</b> process can apply this descriptor,
/// which is the case the product actually runs in.
/// </summary>
/// <remarks>
/// Run from an elevated terminal with <c>dotnet test --filter Category=Admin</c>.
/// <para>
/// <b>What these still do not prove.</b> The connect that matters is a Medium-integrity client reaching a
/// High-integrity server, and one test process cannot be both. Building a medium-IL child means constructing
/// a restricted token by hand, at which point that P/Invoke is the thing under test rather than the pipe. So
/// the automated half stops at "an elevated process applies the label", and the cross-integrity connect is a
/// manual step in <c>tests/MANUAL_CHECKLIST.md</c>. Saying so is better than a test whose name claims more
/// than it checks.
/// </para>
/// </remarks>
[Trait("Category", "Admin")]
public sealed class PipeSecurityAdminTests
{
    private static string UniqueName() => $"AppLedger.admin.{Guid.NewGuid():N}";

    private static bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// A SACL normally needs <c>SE_SECURITY_NAME</c>. Mandatory labels are the exception — writing one at or
    /// below your own integrity needs no privilege — but that is a claim about Windows, and an elevated
    /// process is where it actually has to hold.
    /// </summary>
    [Fact]
    public void Create_FromAnElevatedProcess_StillAppliesTheMediumLabel()
    {
        IsElevated.ShouldBeTrue("run this from an elevated terminal");

        using var server = NamedPipeServerFactory.Create(UniqueName(), maxInstances: 4);

        var applied = NamedPipeServerFactory.ReadAppliedSddl(server.SafePipeHandle);

        applied.ShouldNotBeNull();
        applied.ShouldContain("(ML;;NW;;;ME)");
    }

    /// <summary>
    /// And that lowering the label really happened: without the explicit ace the pipe would inherit the
    /// creating process's High integrity, which is the failure the whole approach exists to avoid.
    /// </summary>
    [Fact]
    public void Create_FromAnElevatedProcess_DoesNotLeaveAHighIntegrityLabel()
    {
        IsElevated.ShouldBeTrue("run this from an elevated terminal");

        using var server = NamedPipeServerFactory.Create(UniqueName(), maxInstances: 4);

        var applied = NamedPipeServerFactory.ReadAppliedSddl(server.SafePipeHandle)!;

        applied.ShouldNotContain("(ML;;NW;;;HI)");
        applied.ShouldNotContain("(ML;;NW;;;SI)");
    }

    [Fact]
    public async Task Create_FromAnElevatedProcess_StillAcceptsAConnection()
    {
        IsElevated.ShouldBeTrue("run this from an elevated terminal");

        var name = UniqueName();
        using var server = NamedPipeServerFactory.Create(name, maxInstances: 4);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(timeout: 5_000);
        await accepting;

        server.IsConnected.ShouldBeTrue();
    }
}
