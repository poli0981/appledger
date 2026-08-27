using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Storage;

/// <summary>
/// The data root is the only place AppLedger writes, and <see cref="DataRootFiles"/> is the only place it
/// deletes (docs/11_SAFETY_POLICY.md §Things the Agent explicitly does not do). These tests exist so that
/// the "read-only by construction" claim has something enforcing it besides the sentence in the doc.
/// </summary>
public sealed class DataRootTests : IDisposable
{
    private readonly string _scratch;
    private readonly DataRoot _root;
    private readonly DataRootFiles _files;

    public DataRootTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "appledger-dataroot-" + Guid.NewGuid().ToString("N")[..12]);
        _root = new DataRoot(Path.Combine(_scratch, DataRoot.FolderName));
        _files = new DataRootFiles(_root);
        _root.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void EnsureCreated_CreatesTheRootAndItsSubdirectories()
    {
        Directory.Exists(_root.Root).ShouldBeTrue();
        Directory.Exists(_root.LogsDirectory).ShouldBeTrue();
        Directory.Exists(_root.CatalogDirectory).ShouldBeTrue();
        Directory.Exists(_root.IconCacheDirectory).ShouldBeTrue();
    }

    [Fact]
    public void Paths_AllLiveUnderTheRoot()
    {
        string[] paths =
        [
            _root.DatabasePath, _root.SettingsPath, _root.LogsDirectory,
            _root.CatalogDirectory, _root.CacheDirectory, _root.IconCacheDirectory,
        ];

        paths.ShouldAllBe(p => PathRules.IsUnder(p, _root.Root));
    }

    [Fact]
    public void Contains_PathOutsideTheRoot_IsFalse()
    {
        _root.Contains(Path.Combine(_scratch, "elsewhere", "x.txt")).ShouldBeFalse();

        // The sibling-prefix trap: a folder whose name merely starts with the root's name.
        _root.Contains(_root.Root + "Backup").ShouldBeFalse();
    }

    [Fact]
    public void DeleteFile_InsideTheRoot_RemovesIt()
    {
        var file = Path.Combine(_root.CacheDirectory, "scratch.bin");
        Directory.CreateDirectory(_root.CacheDirectory);
        File.WriteAllText(file, "x");

        _files.DeleteFile(file);

        File.Exists(file).ShouldBeFalse();
    }

    /// <summary>
    /// The whole reason this type exists. A purge handed a path outside the root is a bug we want to see,
    /// not one to swallow, so it throws rather than quietly doing nothing.
    /// </summary>
    [Fact]
    public void DeleteFile_OutsideTheRoot_Throws()
    {
        var outside = Path.Combine(_scratch, "not-ours.txt");
        File.WriteAllText(outside, "x");

        Should.Throw<ArgumentException>(() => _files.DeleteFile(outside));

        File.Exists(outside).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"relative\file.txt")]
    public void DeleteFile_HostilePaths_AreRefused(string path) =>
        Should.Throw<ArgumentException>(() => _files.DeleteFile(path));

    [Fact]
    public void DeleteDirectory_TheRootItself_IsRefused() =>
        Should.Throw<ArgumentException>(() => _files.DeleteDirectory(_root.Root, recursive: true));

    [Fact]
    public void DeleteDirectory_ASubdirectory_RemovesIt()
    {
        var directory = Path.Combine(_root.CacheDirectory, "scan");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.bin"), "x");

        _files.DeleteDirectory(directory, recursive: true);

        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void DeleteFileIfExists_MissingFile_IsNotAnError()
    {
        Should.NotThrow(() => _files.DeleteFileIfExists(Path.Combine(_root.CacheDirectory, "never-existed.bin")));
    }

    [Fact]
    public void Default_PointsAtAppLedgerDataUnderLocalAppData()
    {
        DataRoot.Default.Root.ShouldEndWith(DataRoot.FolderName);
        PathRules.IsUnder(DataRoot.Default.Root, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share")]
    [InlineData(@"not\rooted")]
    public void Constructor_UnusableRoot_Throws(string root) =>
        Should.Throw<ArgumentException>(() => new DataRoot(root));
}
