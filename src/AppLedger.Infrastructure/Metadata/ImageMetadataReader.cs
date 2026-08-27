using System.Diagnostics;
using AppLedger.Core.Identity;
using AppLedger.Core.Policy;

namespace AppLedger.Infrastructure.Metadata;

/// <summary>
/// Reads what a PE file says about itself and what Windows says about its signature
/// (docs/03_APP_IDENTITY.md §Metadata enrichment).
/// </summary>
/// <remarks>
/// Tier-0 files are reported <see cref="SignatureStatus.CatalogSigned"/> without any verification at all.
/// That is docs/03's rule, and it is also the honest answer: Windows system binaries are signed through
/// security catalogs rather than embedded signatures, so verifying one as a file would return "unsigned"
/// about a file that is in fact signed.
/// </remarks>
public sealed class ImageMetadataReader : IImageMetadataReader
{
    private readonly IPolicyGuard _policy;

    /// <summary>Creates a reader that consults <paramref name="policy"/> for the Tier-0 short-circuit.</summary>
    public ImageMetadataReader(IPolicyGuard policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <inheritdoc />
    public ImageMetadata Read(string canonicalImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalImagePath);

        var version = ReadVersionInfo(canonicalImagePath);

        if (_policy.TierOf(canonicalImagePath) == PathTier.ProtectedOs)
        {
            return version with { SignatureStatus = SignatureStatus.CatalogSigned };
        }

        var (status, signer) = AuthenticodeReader.Read(canonicalImagePath);
        return version with { SignatureStatus = status, Signer = signer };
    }

    /// <summary>
    /// <c>VS_VERSIONINFO</c> through the BCL. No P/Invoke is needed for this one, and every field is
    /// optional: plenty of shipped executables carry none of them.
    /// </summary>
    private static ImageMetadata ReadVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            return new ImageMetadata
            {
                ProductName = Trim(info.ProductName),
                FileDescription = Trim(info.FileDescription),
                CompanyName = Trim(info.CompanyName),
                ProductVersion = Trim(info.ProductVersion),
                FileVersion = Trim(info.FileVersion),
                LegalCopyright = Trim(info.LegalCopyright),
            };
        }
        catch (FileNotFoundException)
        {
            return ImageMetadata.Empty;
        }
        catch (IOException)
        {
            return ImageMetadata.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ImageMetadata.Empty;
        }
    }

    /// <summary>
    /// Version fields are fixed-size buffers in the file, so they routinely arrive padded with spaces or
    /// empty rather than absent. An empty string in a display name is worse than a null one.
    /// </summary>
    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
