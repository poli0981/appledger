using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace AppLedger.Agent.Tasks;

/// <summary>
/// Builds the Scheduled Task XML from the template in docs/16_PACKAGING_AND_UPDATES.md §Scheduled Task.
/// </summary>
/// <remarks>
/// The substitution is pure and separately testable on purpose: every failure mode here is silent. A task
/// with the wrong <c>UserId</c> spelling registers cleanly and never fires; a task pointing at a
/// version-stamped path works today and breaks after the first update; a file written as UTF-8 is rejected
/// by <c>schtasks</c> with a message that blames the XML rather than the encoding.
/// </remarks>
public static class AgentTaskDefinition
{
    /// <summary>The only task AppLedger ever creates, and the only one it may ever delete.</summary>
    /// <remarks>
    /// docs/11_SAFETY_POLICY.md is explicit: creating, modifying or deleting <i>any</i> other Scheduled Task
    /// is on the list of things the Agent does not do. The name being a constant is what makes that
    /// auditable rather than a convention.
    /// </remarks>
    public const string TaskName = "AppLedger Agent";

    /// <summary>The install folder Velopack keeps stable across updates.</summary>
    public const string InstallFolderName = "AppLedger";

    /// <summary>The version-independent subfolder the task's action must point at.</summary>
    public const string CurrentFolderName = "current";

    /// <summary>The Agent executable's file name.</summary>
    public const string AgentExeName = "AppLedger.Agent.exe";

    /// <summary>
    /// Fills the four placeholders (CLAUDE.md still lists them as unresolved; they are substituted here).
    /// </summary>
    /// <param name="user">
    /// The <c>LogonTrigger</c>'s account, as an account <i>name</i> (<c>DOMAIN\user</c>).
    /// </param>
    /// <param name="userSid">The <c>Principal</c>'s account, as a SID string.</param>
    /// <param name="agentExe">Full path to <c>AppLedger.Agent.exe</c>.</param>
    /// <param name="agentDir">The directory containing it.</param>
    /// <remarks>
    /// The trigger takes a name and the principal takes a SID, and they are not interchangeable. Task
    /// Scheduler accepts several spellings in each slot, which is exactly why putting the wrong one in
    /// registers without complaint and then never fires.
    /// </remarks>
    public static string BuildXml(string user, string userSid, string agentExe, string agentDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        ArgumentException.ThrowIfNullOrEmpty(userSid);
        ArgumentException.ThrowIfNullOrEmpty(agentExe);
        ArgumentException.ThrowIfNullOrEmpty(agentDir);

        return Template()
            .Replace("{{USER}}", Escape(user), StringComparison.Ordinal)
            .Replace("{{USER_SID}}", Escape(userSid), StringComparison.Ordinal)
            .Replace("{{AGENT_EXE}}", Escape(agentExe), StringComparison.Ordinal)
            .Replace("{{AGENT_DIR}}", Escape(agentDir), StringComparison.Ordinal);
    }

    /// <summary>Fills the placeholders from the current account and the stable install folder.</summary>
    public static string BuildXmlForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current token carries no user SID.");

        var exe = ResolveAgentExePath();
        return BuildXml(identity.Name, sid, exe, Path.GetDirectoryName(exe)!);
    }

    /// <summary>
    /// Where the task's action must point: <c>%LOCALAPPDATA%\AppLedger\current\AppLedger.Agent.exe</c>.
    /// </summary>
    /// <remarks>
    /// <b>Computed, never observed.</b> <c>--install-task</c> runs from wherever the UI launched it, which
    /// during an update is a version-stamped Velopack folder. Baking <c>Environment.ProcessPath</c> into the
    /// task makes it work today and fail after the first update — and fail in the worst way, as a task that
    /// exists, is enabled, and silently launches nothing. The stable <c>current\</c> folder exists precisely
    /// to be the thing that is written down (docs/16 §Package).
    /// </remarks>
    public static string ResolveAgentExePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        InstallFolderName,
        CurrentFolderName,
        AgentExeName);

    /// <summary>
    /// Writes the XML where <c>schtasks</c> will read it, as <b>UTF-16 with a BOM</b>.
    /// </summary>
    /// <remarks>
    /// <c>schtasks /XML</c> rejects UTF-8, BOM or not, and reports it as "the task XML is malformed" against
    /// XML that is perfectly well formed. The file lives under <c>DataRoot</c> rather than <c>%TEMP%</c>,
    /// which is what docs/11's single-write-root rule requires — and it means the exact document that was
    /// submitted is still there when a registration fails.
    /// </remarks>
    public static string Write(string directory, string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(xml);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{TaskName}.xml");

        File.WriteAllText(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return path;
    }

    private static string Template()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("AppLedger.Agent.Tasks.AppLedgerAgentTask.xml")
            ?? throw new InvalidOperationException("The Scheduled Task template is missing from the assembly.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// XML-escapes a substituted value.
    /// </summary>
    /// <remarks>
    /// A user name can legitimately contain <c>&amp;</c>, and a path can contain it too. Substituting one
    /// raw produces a document that fails to parse — which at least fails loudly, unlike the rest of the
    /// hazards on this page.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
