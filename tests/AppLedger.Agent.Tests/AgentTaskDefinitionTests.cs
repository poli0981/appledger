using System.Text;
using System.Xml.Linq;
using AppLedger.Agent.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Agent.Tests;

/// <summary>
/// The Scheduled Task definition (docs/16_PACKAGING_AND_UPDATES.md §Scheduled Task).
/// </summary>
/// <remarks>
/// Every failure mode this covers is silent. A task with the wrong <c>UserId</c> spelling registers cleanly
/// and never fires; one pointing at a version-stamped path works until the first update and then launches
/// nothing; a file written as UTF-8 is refused with a message that blames the XML. None of them throws, and
/// none of them is visible in Task Scheduler's UI without knowing what to look for — so the substitution is
/// pure and asserted here rather than being checked by whether the product happens to work.
/// </remarks>
public sealed class AgentTaskDefinitionTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly ITestOutputHelper _output;

    public AgentTaskDefinitionTests(ITestOutputHelper output) => _output = output;

    private static XDocument Build(
        string user = @"CONTOSO\ada",
        string sid = "S-1-5-21-1-2-3-1002",
        string exe = @"C:\Users\ada\AppData\Local\AppLedger\current\AppLedger.Agent.exe",
        string dir = @"C:\Users\ada\AppData\Local\AppLedger\current") =>
        XDocument.Parse(AgentTaskDefinition.BuildXml(user, sid, exe, dir));

    [Fact]
    public void BuildXml_LeavesNoPlaceholderBehind()
    {
        var xml = AgentTaskDefinition.BuildXml(@"CONTOSO\ada", "S-1-5-21-1-2-3-1002", @"C:\x\a.exe", @"C:\x");

        _output.WriteLine(xml);
        xml.ShouldNotContain("{{");
        xml.ShouldNotContain("}}");
    }

    /// <summary>
    /// The trigger takes an account <b>name</b> and the principal takes a <b>SID</b>. Task Scheduler accepts
    /// several spellings in each slot, which is exactly why swapping them registers without complaint and
    /// then never fires.
    /// </summary>
    [Fact]
    public void BuildXml_PutsTheAccountNameOnTheTriggerAndTheSidOnThePrincipal()
    {
        var task = Build();

        task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value
            .ShouldBe(@"CONTOSO\ada");

        task.Descendants(Ns + "Principal").Single().Element(Ns + "UserId")!.Value
            .ShouldBe("S-1-5-21-1-2-3-1002");
    }

    [Fact]
    public void BuildXml_RunsTheAgentWithServe()
    {
        var exec = Build().Descendants(Ns + "Exec").Single();

        exec.Element(Ns + "Command")!.Value.ShouldEndWith(@"\AppLedger.Agent.exe");
        exec.Element(Ns + "Arguments")!.Value.ShouldBe("--serve");
    }

    /// <summary>The settings the budget and the lifecycle depend on, asserted rather than assumed.</summary>
    [Theory]
    [InlineData("MultipleInstancesPolicy", "IgnoreNew")]
    [InlineData("ExecutionTimeLimit", "PT0S")]
    [InlineData("Priority", "7")]
    [InlineData("DisallowStartIfOnBatteries", "false")]
    [InlineData("StopIfGoingOnBatteries", "false")]
    [InlineData("AllowStartOnDemand", "true")]
    public void BuildXml_CarriesTheDocumentedSetting(string element, string expected) =>
        Build().Descendants(Ns + "Settings").Single().Element(Ns + element)!.Value.ShouldBe(expected);

    [Fact]
    public void BuildXml_RunsWithHighestAvailableThroughAnInteractiveToken()
    {
        var principal = Build().Descendants(Ns + "Principal").Single();

        principal.Element(Ns + "RunLevel")!.Value.ShouldBe("HighestAvailable");
        principal.Element(Ns + "LogonType")!.Value.ShouldBe("InteractiveToken");
    }

    /// <summary>The 20-second delay keeps the Agent out of the logon stampede.</summary>
    [Fact]
    public void BuildXml_DelaysTheLogonTrigger() =>
        Build().Descendants(Ns + "LogonTrigger").Single().Element(Ns + "Delay")!.Value.ShouldBe("PT20S");

    [Fact]
    public void BuildXml_RestartsOnFailureThreeTimes()
    {
        var restart = Build().Descendants(Ns + "RestartOnFailure").Single();

        restart.Element(Ns + "Interval")!.Value.ShouldBe("PT1M");
        restart.Element(Ns + "Count")!.Value.ShouldBe("3");
    }

    /// <summary>
    /// An account name can contain an ampersand, and so can a path. Substituting one raw produces a document
    /// that will not parse — which at least fails loudly, unlike everything else on this page.
    /// </summary>
    [Fact]
    public void BuildXml_AccountContainingAnAmpersand_StillParses()
    {
        var task = Build(user: @"CONTOSO\a&b");

        task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value.ShouldBe(@"CONTOSO\a&b");
    }

    [Theory]
    [InlineData("", "sid", "exe", "dir")]
    [InlineData("user", "", "exe", "dir")]
    [InlineData("user", "sid", "", "dir")]
    [InlineData("user", "sid", "exe", "")]
    public void BuildXml_MissingSubstitution_Throws(string user, string sid, string exe, string dir) =>
        Should.Throw<ArgumentException>(() => AgentTaskDefinition.BuildXml(user, sid, exe, dir));

    /// <summary>
    /// Computed, never observed. <c>--install-task</c> runs from wherever the UI launched it, which during an
    /// update is a version-stamped Velopack folder; baking that in makes the task work today and silently
    /// launch nothing after the first update.
    /// </summary>
    [Fact]
    public void ResolveAgentExePath_PointsAtTheStableCurrentFolder()
    {
        var path = AgentTaskDefinition.ResolveAgentExePath();

        _output.WriteLine(path);
        path.ShouldEndWith(@"\AppLedger\current\AppLedger.Agent.exe");
        path.ShouldStartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [Fact]
    public void ResolveAgentExePath_IsNotWhereThisProcessIsRunningFrom() =>
        AgentTaskDefinition.ResolveAgentExePath().ShouldNotBe(Environment.ProcessPath);

    // -- the file on disk ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>schtasks /XML</c> rejects UTF-8, BOM or not, and reports it as "the task XML is malformed" against
    /// a document that is perfectly well formed. The encoding is the whole test.
    /// </summary>
    [Fact]
    public void Write_ProducesUtf16WithAByteOrderMark()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appledger-task-{Guid.NewGuid():N}");
        try
        {
            var path = AgentTaskDefinition.Write(directory, Build().ToString());
            var bytes = File.ReadAllBytes(path);

            // FF FE is the little-endian UTF-16 BOM.
            bytes[0].ShouldBe((byte)0xFF);
            bytes[1].ShouldBe((byte)0xFE);

            File.ReadAllText(path, Encoding.Unicode).ShouldContain("AppLedger.Agent.exe");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Write_NamesTheFileAfterTheTask()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appledger-task-{Guid.NewGuid():N}");
        try
        {
            Path.GetFileName(AgentTaskDefinition.Write(directory, "<Task/>"))
                .ShouldBe($"{AgentTaskDefinition.TaskName}.xml");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// docs/11_SAFETY_POLICY.md puts "create, modify or delete any Scheduled Task other than
    /// <c>AppLedger Agent</c>" on the list of things this product does not do. Every <c>schtasks</c> verb
    /// names the constant and none takes a task name from a caller, which makes that a property of the shape
    /// rather than of anyone's discipline.
    /// </summary>
    [Fact]
    public void SchTasks_ExposesNoWayToNameAnotherTask()
    {
        var parameters = typeof(SchTasks).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .Select(p => p.Name)
            .ToList();

        parameters.ShouldNotContain("taskName");
        parameters.ShouldNotContain("name");
        AgentTaskDefinition.TaskName.ShouldBe("AppLedger Agent");
    }
}
