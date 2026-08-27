using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AppLedger.Core.Identity;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AppLedger.Infrastructure.Metadata;

/// <summary>
/// Asks Windows what it thinks of a file's Authenticode signature, offline.
/// </summary>
/// <remarks>
/// <b>Scope, stated rather than implied.</b> This reads *embedded* signatures only. A file signed through
/// a Windows security catalog with no embedded signature therefore reports
/// <see cref="SignatureStatus.Unsigned"/> unless something above it short-circuits — which is what
/// <see cref="ImageMetadataReader"/> does for Tier-0 files, per docs/03_APP_IDENTITY.md §Metadata
/// enrichment. Catalog-hash lookup (<c>CryptCATAdmin*</c>) is deliberately out of scope for v0.1 and is
/// recorded as a known limitation in docs/24_ADR.md.
/// </remarks>
internal static class AuthenticodeReader
{
    /// <summary>Verifies a file and reports the status plus the signer, when there is one to read.</summary>
    internal static (SignatureStatus Status, string? Signer) Read(string canonicalImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalImagePath);

        if (!File.Exists(canonicalImagePath))
        {
            return (SignatureStatus.Unknown, null);
        }

        var status = Verify(canonicalImagePath);

        // Only an embedded signature carries a certificate we can name a signer from.
        return status is SignatureStatus.Unsigned or SignatureStatus.Unknown
            ? (status, null)
            : (status, ReadSignerSubject(canonicalImagePath));
    }

    private static unsafe SignatureStatus Verify(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);

        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)sizeof(WinTrustFileInfo),
                PcwszFilePath = pathPointer,
            };

            var data = new WinTrustData
            {
                CbStruct = (uint)sizeof(WinTrustData),
                DwUiChoice = WinTrust.UiNone,
                FdwRevocationChecks = WinTrust.RevokeNone,
                DwUnionChoice = WinTrust.ChoiceFile,
                PInfo = (nint)(&fileInfo),
                DwStateAction = WinTrust.StateActionVerify,
                DwProvFlags = WinTrust.OfflineProviderFlags,
            };

            var action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = PInvoke.WinVerifyTrust(HWND.Null, ref action, &data);

            // The provider allocated state during the verify; a second call with STATE_ACTION_CLOSE is the
            // only way to give it back. Skipping it leaks a handle per file we ever look at.
            data.DwStateAction = WinTrust.StateActionClose;
            PInvoke.WinVerifyTrust(HWND.Null, ref action, &data);

            return Map(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    private static SignatureStatus Map(int result) => result switch
    {
        0 => SignatureStatus.Valid,
        WinTrust.TrustNoSignature or WinTrust.TrustSubjectFormUnknown => SignatureStatus.Unsigned,
        WinTrust.CertExpired => SignatureStatus.Expired,
        WinTrust.CertUntrustedRoot
            or WinTrust.CertChaining
            or WinTrust.TrustExplicitDistrust
            or WinTrust.TrustSubjectNotTrusted
            or WinTrust.TrustBadDigest
            or WinTrust.CryptSecuritySettings => SignatureStatus.Untrusted,
        _ => SignatureStatus.Unknown,
    };

    /// <summary>
    /// The subject common name of the signing certificate. Read separately from the verdict because a
    /// signature can be present and untrusted, and the user still wants to see who signed it.
    /// </summary>
    private static string? ReadSignerSubject(string path)
    {
        try
        {
            // CreateFromSignedFile is the only managed way to reach a PE's embedded certificate: the loader
            // that replaces it reads certificate *files*, not signed executables. It is used purely as an
            // extractor here, and the bytes it hands back are parsed through the modern loader.
#pragma warning disable SYSLIB0057
            var raw = X509Certificate.CreateFromSignedFile(path).GetRawCertData();
#pragma warning restore SYSLIB0057

            using var certificate = X509CertificateLoader.LoadCertificate(raw);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
        }
        catch (CryptographicException)
        {
            // No embedded certificate, or one we cannot parse. The status already said what matters.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
