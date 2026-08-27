using System.Diagnostics;

namespace AppLedger.Infrastructure.Tests.TestSupport;

/// <summary>
/// Creates directory junctions for the canonicalization tests.
/// </summary>
/// <remarks>
/// .NET exposes <c>Directory.CreateSymbolicLink</c>, but a symlink needs Developer Mode or
/// <c>SeCreateSymbolicLinkPrivilege</c>, while a *junction* needs neither — and a junction is what
/// docs/11_SAFETY_POLICY.md §Tests actually specifies. There is no managed API for one, so we shell out
/// to <c>mklink /J</c>. Creation is allowed to fail: the caller skips rather than fails, because a policy
/// bug and a locked-down test machine must not look the same in CI.
/// </remarks>
internal static class Junctions
{
    /// <summary>Creates a junction at <paramref name="link"/> pointing at <paramref name="target"/>.</summary>
    /// <returns>True when the junction exists afterwards.</returns>
    internal static bool TryCreate(string link, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                return false;
            }

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
