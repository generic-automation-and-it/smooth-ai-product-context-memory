using System.Security.Cryptography;

namespace SmoothAiProductContextMemory.Infrastructure.Storage;

/// <summary>
/// Content-addressed key derived from the SHA-256 of the raw (uncompressed) content. The on-disk
/// object key is a sharded prefix of the first four hex characters plus the full hash, which keeps a
/// huge flat namespace from landing in one directory while still referencing the address directly.
/// </summary>
public static class Sha256ContentAddress
{
    public static string Compute(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }

    public static string Compute(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }
}
