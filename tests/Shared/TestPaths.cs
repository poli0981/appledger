using System.Reflection;

namespace AppLedger.Testing;

/// <summary>
/// Locates repository files the tests read as fixtures. Walking up to the solution file keeps the tests
/// independent of the output layout, which carries a platform segment (ADR-16).
/// </summary>
/// <remarks>
/// Linked into every test project rather than shared through a package: a helper that finds the repo root
/// has no business being a build artifact, and both test assemblies need the identical rules for the
/// shared corpora under <c>tests/fixtures/</c> (docs/19_TESTING.md §Fixtures).
/// </remarks>
internal static class TestPaths
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);

    /// <summary>The repository root, found by walking up to <c>AppLedger.slnx</c>.</summary>
    internal static string RepoRoot => RepoRootLazy.Value;

    /// <summary>The shipped catalog, which is itself a fixture (docs/19_TESTING.md §Fixtures).</summary>
    internal static string SeedCatalog => Path.Combine(RepoRoot, "catalog", "appledger-catalog.json");

    /// <summary>The vendored Public Suffix List.</summary>
    internal static string PublicSuffixList => Path.Combine(RepoRoot, "catalog", "public_suffix_list.dat");

    /// <summary>A file inside the shared minisign corpus.</summary>
    internal static string Minisign(string fileName) =>
        Path.Combine(RepoRoot, "tests", "fixtures", "minisign", fileName);

    /// <summary>
    /// A file inside the calling test project's own fixture folders. The project directory is derived from
    /// the calling assembly's name, so the same call works from every test project.
    /// </summary>
    internal static string Fixture(params string[] parts)
    {
        var project = Assembly.GetCallingAssembly().GetName().Name
            ?? throw new InvalidOperationException("The calling test assembly has no name.");

        return Path.Combine([RepoRoot, "tests", project, .. parts]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AppLedger.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find AppLedger.slnx above '{AppContext.BaseDirectory}'. Tests read repository files directly.");
    }
}
