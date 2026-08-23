namespace AppLedger.Core.Tests;

/// <summary>
/// Locates repository files the tests read as fixtures. Walking up to the solution file keeps the tests
/// independent of the output layout, which now carries a platform segment (ADR-16).
/// </summary>
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

    /// <summary>A file inside this project's own fixture folders.</summary>
    internal static string Fixture(params string[] parts) =>
        Path.Combine([RepoRoot, "tests", "AppLedger.Core.Tests", .. parts]);

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
