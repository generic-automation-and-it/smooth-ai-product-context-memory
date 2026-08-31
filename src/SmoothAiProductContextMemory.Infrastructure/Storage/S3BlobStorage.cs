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

            Stream decompressed = BlobCompressor.Decompress(download.ToArray());
            return new BlobContent(decompressed, contentType);
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

        MakeBucketArgs makeArgs = new MakeBucketArgs().WithBucket(_bucket);
        await _client.MakeBucketAsync(makeArgs, cancellationToken);
    }

    private static string ToObjectKey(string address) => address;

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
