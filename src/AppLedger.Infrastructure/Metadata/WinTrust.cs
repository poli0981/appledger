using System.Runtime.InteropServices;

namespace AppLedger.Infrastructure.Metadata;

/// <summary>
/// The two <c>wintrust.h</c> structures <c>WinVerifyTrust</c> needs. They are hand-written because the
/// API takes them as <c>void*</c>, so CsWin32 has no dependency to follow and generates neither.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustFileInfo
{
    /// <summary>Size of this structure, in bytes.</summary>
    internal uint CbStruct;

    /// <summary>The file to verify.</summary>
    internal nint PcwszFilePath;

    /// <summary>An already-open handle to the file, or null to let wintrust open it.</summary>
    internal nint HFile;

    /// <summary>A known subject GUID, or null.</summary>
    internal nint PgKnownSubject;
}

/// <summary>The <c>WINTRUST_DATA</c> block, in its file-verification shape.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustData
{
    /// <summary>Size of this structure, in bytes.</summary>
    internal uint CbStruct;

    /// <summary>Policy-provider callback data. Unused.</summary>
    internal nint PPolicyCallbackData;

    /// <summary>SIP client data. Unused.</summary>
    internal nint PSipClientData;

    /// <summary>UI choice. Always <see cref="WinTrust.UiNone"/> — an Agent never shows a dialog.</summary>
    internal uint DwUiChoice;

    /// <summary>Revocation checks. Always <see cref="WinTrust.RevokeNone"/>: revocation means network.</summary>
    internal uint FdwRevocationChecks;

    /// <summary>Which member of the union below is in use.</summary>
    internal uint DwUnionChoice;

    /// <summary>Pointer to a <see cref="WinTrustFileInfo"/> for <see cref="WinTrust.ChoiceFile"/>.</summary>
    internal nint PInfo;

    /// <summary>Verify, then close, so the provider frees its state.</summary>
    internal uint DwStateAction;

    /// <summary>Provider state, filled in by the verify call and consumed by the close call.</summary>
    internal nint HwvtStateData;

    /// <summary>URL reference. Unused.</summary>
    internal nint PwszUrlReference;

    /// <summary>Provider flags.</summary>
    internal uint DwProvFlags;

    /// <summary>UI context.</summary>
    internal uint DwUiContext;

    /// <summary>Signature settings. Unused.</summary>
    internal nint PSignatureSettings;
}

/// <summary>The <c>WinVerifyTrust</c> constants and result codes AppLedger cares about.</summary>
internal static class WinTrust
{
    /// <summary>Never show a dialog.</summary>
    internal const uint UiNone = 2;

    /// <summary>No revocation check at all.</summary>
    internal const uint RevokeNone = 0;

    /// <summary>The union carries a <see cref="WinTrustFileInfo"/>.</summary>
    internal const uint ChoiceFile = 1;

    /// <summary>Run the verification and keep the state for a follow-up close.</summary>
    internal const uint StateActionVerify = 1;

    /// <summary>Release the state the verify call allocated.</summary>
    internal const uint StateActionClose = 2;

    /// <summary>
    /// <c>WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL</c>. Together they guarantee the call
    /// makes no network request, which docs/12_PRIVACY_AND_RETENTION.md §Network calls requires: that list
    /// is exhaustive and signature verification is not on it.
    /// </summary>
    internal const uint OfflineProviderFlags = 0x00000010 | 0x00001000;

    /// <summary>Nothing is signed here.</summary>
    internal const int TrustNoSignature = unchecked((int)0x800B0100);

    /// <summary>The signature does not match the file's contents.</summary>
    internal const int TrustBadDigest = unchecked((int)0x80096010);

    /// <summary>The signing certificate has expired and there is no valid countersignature.</summary>
    internal const int CertExpired = unchecked((int)0x800B0101);

    /// <summary>The chain ends in a root the machine does not trust.</summary>
    internal const int CertUntrustedRoot = unchecked((int)0x800B0109);

    /// <summary>The chain could not be built.</summary>
    internal const int CertChaining = unchecked((int)0x800B010A);

    /// <summary>The user or an administrator explicitly distrusted the publisher.</summary>
    internal const int TrustExplicitDistrust = unchecked((int)0x800B0111);

    /// <summary>The subject is signed but not trusted for this action.</summary>
    internal const int TrustSubjectNotTrusted = unchecked((int)0x800B0004);

    /// <summary>Local security policy refused the signature.</summary>
    internal const int CryptSecuritySettings = unchecked((int)0x80092026);

    /// <summary>No provider could handle the file, which for our purposes means "not a signed PE".</summary>
    internal const int TrustSubjectFormUnknown = unchecked((int)0x800B0003);

    /// <summary>The file could not be opened.</summary>
    internal const int TrustFileNotFound = unchecked((int)0x800B0100);
}
