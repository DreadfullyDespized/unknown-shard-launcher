using System.Security.Cryptography;

namespace ShardLauncher.Patching;

public static class ManifestSigner
{
    public const DSASignatureFormat Format = DSASignatureFormat.Rfc3279DerSequence;
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static byte[] Sign(ReadOnlySpan<byte> data, string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        EnsureP256(ec);
        return ec.SignData(data, HashAlgorithmName.SHA256, Format);
    }

    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            EnsureP256(ec);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, Format);
        }
        catch (CryptographicException) { return false; }
    }

    public static byte[] DecodeSigFile(string text)
    {
        try { return Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { throw new ManifestValidationException("signature is not base64"); }
    }

    public static string Fingerprint(string publicKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(publicKeyPem);
        EnsureP256(ec);
        return Hashing.Sha256Hex(ec.ExportSubjectPublicKeyInfo());
    }

    public static PatchManifest VerifyAndParse(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature,
        IReadOnlyDictionary<string, string> trustedKeysById)
    {
        string? keyId = null;
        foreach (var (id, pem) in trustedKeysById)
            if (Verify(manifestBytes, signature, pem)) { keyId = id; break; }
        if (keyId is null) throw new ManifestValidationException("signature does not verify with any trusted key");
        var m = ManifestJson.Parse(manifestBytes);
        if (m.SigningKeyId != keyId) throw new ManifestValidationException($"signing_key_id '{m.SigningKeyId}' != verifying key '{keyId}'");
        return m;
    }

    private static void EnsureP256(ECDsa ec)
    {
        var p = ec.ExportParameters(false);
        if (ec.KeySize != 256 || p.Curve.Oid?.Value != P256Oid) throw new CryptographicException("key is not ECDSA P-256");
    }
}
