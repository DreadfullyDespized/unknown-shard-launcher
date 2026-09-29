using System.Security.Cryptography;

namespace UnknownShard.Patching;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static (string Sha256, long Size) HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA256.HashData(fs);
        return (Convert.ToHexString(hash).ToLowerInvariant(), fs.Length);
    }
}
