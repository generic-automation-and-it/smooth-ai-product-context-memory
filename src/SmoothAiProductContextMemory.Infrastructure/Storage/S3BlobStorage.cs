using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Storage;

/// <summary>
/// S3-compatible (MinIO) implementation of <see cref="IBlobStorage"/>. Content is gzip-compressed
/// client-side and content-addressed by SHA-256 of the uncompressed bytes. Buckets, object keys and
/// ETags never escape this class, so the backing store stays swappable.
/// </summary>
public sealed class S3BlobStorage : IBlobStorage, IAsyncDisposable
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private readonly ILogger<S3BlobStorage> _logger;

    public S3BlobStorage(IOptions<BlobStorageOptions> options, ILogger<S3BlobStorage> logger)
    {
        BlobStorageOptions opts = options.Value;
        Uri endpointUri = new(opts.Endpoint);

        _client = new MinioClient()
            .WithEndpoint(endpointUri.Host, endpointUri.Port)
            .WithCredentials(opts.AccessKey, opts.SecretKey)
            .WithSSL(endpointUri.Scheme == Uri.UriSchemeHttps)
            .Build();
        _bucket = opts.Bucket;
        _logger = logger;
    }

    public async Task<string> StoreAsync(Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] raw = await ReadAllAsync(content, cancellationToken);
        string address = Sha256ContentAddress.Compute(raw);
        string objectKey = ToObjectKey(address);

        if (await ObjectExistsAsync(objectKey, cancellationToken))
        {
            _logger.LogDebug("Blob {Address} already stored — skipping upload", address);
            return address;
        }

        await EnsureBucketAsync(cancellationToken);

        byte[] compressed = BlobCompressor.Compress(new MemoryStream(raw, writable: false));
        PutObjectArgs args = new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithStreamData(new MemoryStream(compressed, writable: false))
            .WithObjectSize(compressed.Length)
            .WithContentType(contentType ?? "application/octet-stream")
            .WithHeaders(new Dictionary<string, string>
            {
                ["x-amz-meta-encoding"] = BlobCompressor.EncodingName,
                ["x-amz-meta-sha256"] = address.Split('/')[^1],
            });

        await _client.PutObjectAsync(args, cancellationToken);

        _logger.LogDebug("Stored blob {Address} ({Bytes} raw bytes)", address, raw.Length);
        return address;
    }

    public async Task<BlobContent?> GetAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            using var download = new MemoryStream();
            GetObjectArgs args = new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(ToObjectKey(address))
                .WithCallbackStream(stream => stream.CopyTo(download));

            ObjectStat response = await _client.GetObjectAsync(args, cancellationToken);
            string? contentType = response.ContentType;

            byte[] body = download.ToArray();
            bool isGzip = string.Equals(GetMetadataValue(response.MetaData, "encoding"), BlobCompressor.EncodingName, StringComparison.OrdinalIgnoreCase);
            Stream content = isGzip ? BlobCompressor.Decompress(body) : new MemoryStream(body, writable: false);
            return new BlobContent(content, contentType);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
        catch (BucketNotFoundException)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string address, CancellationToken cancellationToken = default)
        => await ObjectExistsAsync(ToObjectKey(address), cancellationToken);

    public async Task DeleteAsync(string address, CancellationToken cancellationToken = default)
    {
        RemoveObjectArgs args = new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(ToObjectKey(address));

        await _client.RemoveObjectAsync(args, cancellationToken);
        _logger.LogDebug("Deleted blob {Address}", address);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            StatObjectArgs args = new StatObjectArgs()
                .WithBucket(_bucket)
                .WithObject(objectKey);

            await _client.StatObjectAsync(args, cancellationToken);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (BucketNotFoundException)
        {
            return false;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        BucketExistsArgs existsArgs = new BucketExistsArgs().WithBucket(_bucket);
        if (await _client.BucketExistsAsync(existsArgs, cancellationToken))
        {
            return;
        }

        try
        {
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), cancellationToken);
        }
        catch (Minio.Exceptions.MinioException)
        {
            // A concurrent writer may have created the bucket between the exists check and MakeBucket.
            if (!await _client.BucketExistsAsync(existsArgs, cancellationToken))
            {
                throw;
            }
        }
    }

    private static string ToObjectKey(string address) => address;

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string keySuffix)
    {
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            if (entry.Key.EndsWith(keySuffix, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
