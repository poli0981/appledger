using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace AppLedger.Core.Catalog;

/// <summary>The signature algorithm tag of a minisign file.</summary>
public enum MinisignAlgorithm
{
    /// <summary>Legacy <c>Ed</c>: the signature covers the raw file. AppLedger rejects this form.</summary>
    Legacy,

    /// <summary>Prehashed <c>ED</c>: the signature covers BLAKE2b-512 of the file. The only form we accept.</summary>
    Prehashed,
}

/// <summary>
/// A parsed minisign public key: the two-byte algorithm tag, the key id, and the Ed25519 public key.
/// </summary>
public sealed record MinisignPublicKey
{
    /// <summary>The 8-byte key id, compared against a signature's key id before any crypto runs.</summary>
    public required IReadOnlyList<byte> KeyId { get; init; }

    /// <summary>The 32-byte Ed25519 public key.</summary>
    public required IReadOnlyList<byte> PublicKey { get; init; }

    /// <summary>The untrusted comment line, kept for display in Settings › Catalog.</summary>
    public string UntrustedComment { get; init; } = string.Empty;

    /// <summary>The key id in minisign's display form: byte-reversed, upper-case hex.</summary>
    public string KeyIdHex => MinisignSignature.FormatKeyId(KeyId);
}

/// <summary>
/// A parsed minisign detached signature. Parsing is pure text and base64 work and therefore lives in
/// Core; the Ed25519 and BLAKE2b-512 verification needs a crypto library and lives in Infrastructure
/// behind <see cref="ICatalogVerifier"/> (docs/13_CATALOG_RULES.md §Signing &amp; verification).
/// </summary>
public sealed record MinisignSignature
{
    /// <summary>Whether the signature covers the file or its BLAKE2b-512 hash.</summary>
    public required MinisignAlgorithm Algorithm { get; init; }

    /// <summary>The 8-byte key id the signature claims.</summary>
    public required IReadOnlyList<byte> KeyId { get; init; }

    /// <summary>The 64-byte Ed25519 signature.</summary>
    public required IReadOnlyList<byte> Signature { get; init; }

    /// <summary>
    /// The trusted comment. It is covered by <see cref="GlobalSignature"/>, so unlike the untrusted
    /// comment it cannot be edited without invalidating the file.
    /// </summary>
    public required string TrustedComment { get; init; }

    /// <summary>The 64-byte signature over <c>signature || utf8(trusted comment)</c>.</summary>
    public required IReadOnlyList<byte> GlobalSignature { get; init; }

    /// <summary>The untrusted comment line, which nothing covers and nothing should trust.</summary>
    public string UntrustedComment { get; init; } = string.Empty;

    /// <summary>The key id in minisign's display form.</summary>
    public string KeyIdHex => FormatKeyId(KeyId);

    /// <summary>
    /// The exact bytes the global signature must be verified against: the signature followed by the UTF-8
    /// trusted comment. Building it here keeps the layout in one place.
    /// </summary>
    public byte[] GlobalSignedData()
    {
        var comment = Encoding.UTF8.GetBytes(TrustedComment);
        var buffer = new byte[Signature.Count + comment.Length];
        for (var i = 0; i < Signature.Count; i++)
        {
            buffer[i] = Signature[i];
        }

        comment.CopyTo(buffer, Signature.Count);
        return buffer;
    }

    private const string UntrustedPrefix = "untrusted comment: ";
    private const string TrustedPrefix = "trusted comment: ";

    /// <summary>Parses a <c>.minisig</c> file. Throws <see cref="FormatException"/> on any deviation.</summary>
    public static MinisignSignature Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = SplitLines(content);

        if (lines.Count < 4)
        {
            throw new FormatException("A minisign signature has four lines: two comments, each followed by base64.");
        }

        if (!lines[0].StartsWith(UntrustedPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Line 1 must start with 'untrusted comment: '.");
        }

        if (!lines[2].StartsWith(TrustedPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Line 3 must start with 'trusted comment: '.");
        }

        var blob = DecodeBase64(lines[1], "signature");
        if (blob.Length != 2 + 8 + 64)
        {
            throw new FormatException($"Signature blob must be 74 bytes, got {blob.Length}.");
        }

        var algorithm = ReadAlgorithm(blob[0], blob[1]);
        var globalSignature = DecodeBase64(lines[3], "global signature");
        if (globalSignature.Length != 64)
        {
            throw new FormatException($"Global signature must be 64 bytes, got {globalSignature.Length}.");
        }

        return new MinisignSignature
        {
            Algorithm = algorithm,
            KeyId = blob[2..10],
            Signature = blob[10..74],
            TrustedComment = lines[2][TrustedPrefix.Length..],
            GlobalSignature = globalSignature,
            UntrustedComment = lines[0][UntrustedPrefix.Length..],
        };
    }

    /// <summary>Parses a <c>.pub</c> file. Throws <see cref="FormatException"/> on any deviation.</summary>
    public static MinisignPublicKey ParsePublicKey(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = SplitLines(content);

        if (lines.Count < 2)
        {
            throw new FormatException("A minisign public key has a comment line followed by base64.");
        }

        var payloadLine = lines[0].StartsWith(UntrustedPrefix, StringComparison.Ordinal) ? lines[1] : lines[0];
        var comment = lines[0].StartsWith(UntrustedPrefix, StringComparison.Ordinal)
            ? lines[0][UntrustedPrefix.Length..]
            : string.Empty;

        var blob = DecodeBase64(payloadLine, "public key");
        if (blob.Length != 2 + 8 + 32)
        {
            throw new FormatException($"Public key blob must be 42 bytes, got {blob.Length}.");
        }

        // The key file always carries the key algorithm 'Ed'; the prehashed/legacy choice belongs to a signature.
        if (blob[0] != (byte)'E' || blob[1] != (byte)'d')
        {
            throw new FormatException("Public key algorithm must be 'Ed'.");
        }

        return new MinisignPublicKey
        {
            KeyId = blob[2..10],
            PublicKey = blob[10..42],
            UntrustedComment = comment,
        };
    }

    /// <summary>Parses without throwing.</summary>
    public static bool TryParse(string content, [NotNullWhen(true)] out MinisignSignature? signature, out string? error)
    {
        try
        {
            signature = Parse(content);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            signature = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// True when the signature was made by the given key. Checked before any crypto, so a signature from
    /// an unrelated key is refused without spending a verification.
    /// </summary>
    public bool MatchesKey(MinisignPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (KeyId.Count != key.KeyId.Count)
        {
            return false;
        }

        for (var i = 0; i < KeyId.Count; i++)
        {
            if (KeyId[i] != key.KeyId[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Renders a key id the way minisign prints it: byte-reversed, upper-case hex.</summary>
    public static string FormatKeyId(IReadOnlyList<byte> keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        var sb = new StringBuilder(keyId.Count * 2);
        for (var i = keyId.Count - 1; i >= 0; i--)
        {
            sb.Append(keyId[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static MinisignAlgorithm ReadAlgorithm(byte first, byte second)
    {
        if (first != (byte)'E')
        {
            throw new FormatException("Signature algorithm must start with 'E'.");
        }

        return second switch
        {
            (byte)'D' => MinisignAlgorithm.Prehashed,
            (byte)'d' => MinisignAlgorithm.Legacy,
            _ => throw new FormatException("Signature algorithm must be 'ED' (prehashed) or 'Ed' (legacy)."),
        };
    }

    private static byte[] DecodeBase64(string line, string what)
    {
        try
        {
            return Convert.FromBase64String(line.Trim());
        }
        catch (FormatException ex)
        {
            throw new FormatException($"The {what} line is not valid base64.", ex);
        }
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>(4);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        return lines;
    }
}
