using System.IO.Compression;

namespace SmoothAiProductContextMemory.Infrastructure.Storage;

/// <summary>
/// Client-side compression applied before upload and reversed on read, so behaviour is identical
/// across storage backends regardless of any server-side compression the engine may or may not apply.
/// </summary>
internal sealed class BlobCompressor
{
    public const string EncodingName = "gzip";

    /// <summary>Compresses an entire stream and returns the compressed bytes in memory.</summary>
    public static byte[] Compress(Stream source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            source.CopyTo(gzip);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses gzip bytes into a readable stream.</summary>
    public static Stream Decompress(byte[] compressed)
        => new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
}
