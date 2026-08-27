using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Repo;

/// <summary>
/// Guards against two classes of defect that cost real time at kickoff and leave no trace in a diff
/// review (docs/24_ADR.md §Findings). Both are cheap to check and impossible to notice by reading.
/// </summary>
public sealed partial class RepositoryGuardTests
{
    private static readonly string[] XmlExtensions = [".csproj", ".props", ".targets", ".slnx", ".manifest", ".xaml", ".resx"];

    private static readonly string[] TextExtensions =
        [".cs", ".csproj", ".props", ".targets", ".slnx", ".json", ".txt", ".yml", ".yaml", ".xaml", ".manifest", ".md"];

    private static readonly string[] SkippedDirectories = [".git", "obj", "bin", ".idea", ".vs"];

    /// <summary>
    /// XML forbids <c>--</c> inside a comment. Documenting a CLI switch such as <c>--status</c> in a
    /// project-file comment makes the file unloadable, and MSBuild reports it as a parse error a long way
    /// from the cause.
    /// </summary>
    [Fact]
    public void XmlFiles_HaveNoDoubleDashInsideComments()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateFiles(XmlExtensions))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in XmlComment().Matches(content))
            {
                var body = match.Groups[1].Value;
                if (body.Contains("--", StringComparison.Ordinal) || body.EndsWith('-'))
                {
                    var line = content[..match.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Relative(file)}:{line}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "XML comments cannot contain '--'; reword the comment instead of writing a CLI switch verbatim");
    }

    /// <summary>
    /// A non-breaking space that slips into a string literal produces output that looks right and compares
    /// wrong. It happened once in <c>ByteFormatter</c> and was only caught because a test compared exact
    /// strings.
    /// </summary>
    [Fact]
    public void SourceFiles_HaveNoInvisibleCharacters()
    {
        int[] suspects = [0x00A0, 0x2007, 0x202F, 0x200B, 0x200E, 0x200F, 0xFEFF, 0x2028, 0x2029];
        var offenders = new List<string>();

        foreach (var file in EnumerateFiles(TextExtensions))
        {
            var content = File.ReadAllText(file);
            for (var i = 0; i < content.Length; i++)
            {
                if (!suspects.Contains(content[i]))
                {
                    continue;
                }

                var line = content[..i].Count(c => c == '\n') + 1;
                offenders.Add($"{Relative(file)}:{line} U+{(int)content[i]:X4}");
                break;
            }
        }

        offenders.ShouldBeEmpty("invisible characters in source produce output that looks right and compares wrong");
    }

    /// <summary>
    /// Every project must declare the two supported platforms. A project that forgets falls back to AnyCPU,
    /// which makes CsWin32 refuse architecture-specific APIs (ADR-16).
    /// </summary>
    [Fact]
    public void EveryProject_DeclaresTheSupportedPlatforms()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateFiles([".csproj"]))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("<Platforms>x64;ARM64</Platforms>", StringComparison.Ordinal))
            {
                offenders.Add(Relative(file));
            }
        }

        offenders.ShouldBeEmpty("add <Platforms>x64;ARM64</Platforms> and the matching mapping in AppLedger.slnx");
    }

    /// <summary>
    /// The banned-symbols list is the build-time half of the observer principle. If it ever loses the
    /// PROCESS_* entries again, the safety story stops being enforced by anything but review.
    /// </summary>
    [Fact]
    public void BannedSymbols_StillBansEveryForbiddenProcessRight()
    {
        var banned = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "BannedSymbols.txt"));

        string[] forbidden =
        [
            "PROCESS_VM_READ", "PROCESS_VM_WRITE", "PROCESS_VM_OPERATION", "PROCESS_CREATE_THREAD",
            "PROCESS_CREATE_PROCESS", "PROCESS_DUP_HANDLE", "PROCESS_SET_INFORMATION", "PROCESS_SET_QUOTA",
            "PROCESS_TERMINATE", "PROCESS_SUSPEND_RESUME", "PROCESS_QUERY_INFORMATION", "PROCESS_ALL_ACCESS",
        ];

        foreach (var right in forbidden)
        {
            banned.ShouldContain(right, Case.Sensitive, $"{right} must stay banned (docs/11_SAFETY_POLICY.md)");
        }

        banned.ShouldNotContain(
            "PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION;",
            Case.Sensitive,
            "the one right we are allowed to request must not be banned");
    }

    /// <summary>
    /// docs/11_SAFETY_POLICY.md asks for exactly this: a scan of every <c>OpenProcess</c> call site
    /// asserting the rights constant. The banned-symbols analyzer stops the other enum members from being
    /// named at all; this stops a future call site from passing a raw numeric mask instead.
    /// </summary>
    [Fact]
    public void EveryOpenProcessCallSite_RequestsOnlyQueryLimitedInformation()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles("src"))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in OpenProcessCall().Matches(content))
            {
                var window = content.Substring(match.Index, Math.Min(240, content.Length - match.Index));
                if (!window.Contains("PROCESS_QUERY_LIMITED_INFORMATION", StringComparison.Ordinal))
                {
                    var line = content[..match.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Relative(file)}:{line}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "PROCESS_QUERY_LIMITED_INFORMATION is the only right AppLedger ever requests (docs/11_SAFETY_POLICY.md)");
    }

    /// <summary>
    /// <c>File.Delete</c> and <c>Directory.Delete</c> are banned so that deletion goes through one audited
    /// helper. A second file suppressing RS0030 would reopen the hole quietly, which is why
    /// <c>BannedSymbols.txt</c> names the exempt file by path rather than trusting review.
    /// </summary>
    [Fact]
    public void OnlyDataRootFiles_SuppressesTheBannedApiAnalyzer()
    {
        var offenders = EnumerateSourceFiles("src")
            .Where(f => File.ReadAllText(f).Contains("RS0030", StringComparison.Ordinal))
            .Select(Relative)
            .Where(f => !f.EndsWith("DataRootFiles.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.ShouldBeEmpty("only Infrastructure/Storage/DataRootFiles.cs may suppress RS0030");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string topLevelDirectory) =>
        EnumerateFiles([".cs"])
            .Where(f => Relative(f).StartsWith(topLevelDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> EnumerateFiles(IReadOnlyList<string> extensions) =>
        Directory.EnumerateFiles(TestPaths.RepoRoot, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Relative(f).Split(Path.DirectorySeparatorChar, '/').Any(SkippedDirectories.Contains))
            // Vendored third-party bytes are not ours to reformat.
            .Where(f => !f.Contains("public_suffix_list", StringComparison.OrdinalIgnoreCase));

    private static string Relative(string file) => Path.GetRelativePath(TestPaths.RepoRoot, file);

    [GeneratedRegex(@"<!--(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    // A plain word boundary is not enough: OnOpenProcess( and OpenProcessToken( would both match.
    // The look-behind excludes an identifier character before the name, and requiring the open
    // parenthesis immediately after it excludes the Token suffix.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])OpenProcess\s*\(")]
    private static partial Regex OpenProcessCall();
}
