using AppLedger.Core.Identity;
using AppLedger.Infrastructure.Metadata;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using AppLedger.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Metadata;

/// <summary>
/// Adapter smoke test for PE version information and <c>WinVerifyTrust</c>
/// (docs/19_TESTING.md §Layers).
/// </summary>
public sealed class ImageMetadataReaderTests : IDisposable
{
    private readonly string _scratch;
    private readonly ImageMetadataReader _reader;

    public ImageMetadataReaderTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "appledger-metadata-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_scratch);

        var dataRoot = new DataRoot(Path.Combine(_scratch, DataRoot.FolderName));
        _reader = new ImageMetadataReader(PolicyGuard.Create(catalog: null, dataRoot: dataRoot));
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

    /// <summary>
    /// A Windows system binary. docs/19 names <c>notepad.exe</c>; anything under System32 exercises the
    /// same path, so the test falls back rather than depending on which optional features are installed.
    /// </summary>
    private static string SystemBinary
    {
        get
        {
            var system32 = KnownFolders.Current.System32
                ?? throw new InvalidOperationException("FOLDERID_System did not resolve.");

            foreach (var candidate in new[] { "notepad.exe", "where.exe", "cmd.exe" })
            {
                var path = Path.Combine(system32, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new InvalidOperationException("No system binary was found to read metadata from.");
        }
    }

    /// <summary>
    /// Tier-0 files short-circuit to <see cref="SignatureStatus.CatalogSigned"/> without computing catalog
    /// hashes (docs/03_APP_IDENTITY.md §Metadata enrichment). Verifying one as a file would report
    /// "unsigned" about a file that is in fact signed, which is worse than not asking.
    /// </summary>
    [Fact]
    public void Read_SystemBinary_IsCatalogSignedWithoutVerification()
    {
        var metadata = _reader.Read(SystemBinary);

        metadata.SignatureStatus.ShouldBe(SignatureStatus.CatalogSigned);
        metadata.Signer.ShouldBeNull();
    }

    /// <summary>
    /// Version fields are asserted as present rather than by value: <c>ProductName</c> and
    /// <c>CompanyName</c> are localized, so comparing them would fail on a Japanese Windows — one of the
    /// boxes in the manual matrix (docs/19_TESTING.md).
    /// </summary>
    [Fact]
    public void Read_SystemBinary_ReadsVersionInformationInACultureIndependentWay()
    {
        var metadata = _reader.Read(SystemBinary);

        metadata.FileVersion.ShouldNotBeNullOrWhiteSpace();
        metadata.ProductVersion.ShouldNotBeNullOrWhiteSpace();
        metadata.ProductName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Read_FileWithNoSignature_IsUnsigned()
    {
        var path = Path.Combine(_scratch, "unsigned.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);

        var metadata = _reader.Read(path);

        metadata.SignatureStatus.ShouldBe(SignatureStatus.Unsigned);
        metadata.Signer.ShouldBeNull();
    }

    [Fact]
    public void Read_MissingFile_IsUnknownRatherThanThrowing()
    {
        var metadata = _reader.Read(Path.Combine(_scratch, "not-here.exe"));

        metadata.SignatureStatus.ShouldBe(SignatureStatus.Unknown);
        metadata.ProductName.ShouldBeNull();
    }

    /// <summary>
    /// A file with no version resource at all: every field null, nothing thrown, and an empty string never
    /// substituted for a missing name.
    /// </summary>
    [Fact]
    public void Read_FileWithNoVersionResource_ReturnsNullsNotEmptyStrings()
    {
        var path = Path.Combine(_scratch, "plain.exe");
        File.WriteAllText(path, "not really a PE");

        var metadata = _reader.Read(path);

        metadata.ProductName.ShouldBeNull();
        metadata.CompanyName.ShouldBeNull();
        metadata.FileDescription.ShouldBeNull();
    }

    /// <summary>
    /// Verification must not leak the provider state <c>WinVerifyTrust</c> allocates. Running it many
    /// times over is the cheap way to notice: a leak shows up as a growing handle count.
    /// </summary>
    [Fact]
    public void Read_RepeatedVerification_DoesNotLeakProviderState()
    {
        var path = Path.Combine(_scratch, "repeat.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _reader.Read(path);
        self.Refresh();
        var before = self.HandleCount;

        for (var i = 0; i < 200; i++)
        {
            _reader.Read(path);
        }

        self.Refresh();
        (self.HandleCount - before).ShouldBeLessThan(100, "WinVerifyTrust state must be closed after each verify");
    }
}
