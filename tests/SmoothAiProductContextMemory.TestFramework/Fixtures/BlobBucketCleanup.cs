using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;

namespace SmoothAiProductContextMemory.TestFramework.Fixtures;

/// <summary>
/// Deletes an isolated test MinIO bucket so repeated test runs do not accumulate orphaned buckets on
/// the persistent test blob container. MinIO refuses to remove a non-empty bucket, so the objects are
/// drained first. A failure is reported through the caller's reporter (typically the test output
/// helper) rather than swallowed, so an inert cleanup cannot hide behind a green run. Domain-agnostic:
/// takes the S3 endpoint/credentials and a bucket name, knows nothing about the application.
/// </summary>
public static class BlobBucketCleanup
{
    public static async Task DeleteBlobBucketAsync(
        string endpoint,
        string accessKey,
        string secretKey,
        string bucket,
        Action<string>? report = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointUri = new Uri(endpoint);
            var client = new MinioClient()
                .WithEndpoint(endpointUri.Host, endpointUri.Port)
                .WithCredentials(accessKey, secretKey)
                .WithSSL(endpointUri.Scheme == Uri.UriSchemeHttps)
                .Build();

            if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken))
            {
                return;
            }

            // A non-empty bucket cannot be removed, so delete its objects first (tests write bodies into
            // the per-test bucket).
            await foreach (Item item in client.ListObjectsEnumAsync(
                new ListObjectsArgs().WithBucket(bucket).WithRecursive(true), cancellationToken))
            {
                await client.RemoveObjectAsync(
                    new RemoveObjectArgs().WithBucket(bucket).WithObject(item.Key), cancellationToken);
            }

            await client.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket), cancellationToken);
        }
        catch (Exception exception)
        {
            report?.Invoke($"failed to remove bucket '{bucket}': {exception.Message}");
        }
    }
}
