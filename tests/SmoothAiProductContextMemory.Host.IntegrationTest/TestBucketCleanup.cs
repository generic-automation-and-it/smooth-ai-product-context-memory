using Minio;
using Minio.DataModel.Args;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// Deletes a per-test MinIO bucket so repeated integration runs do not accumulate orphaned buckets on
/// the persistent test blob container. The product's blob store is MinIO speaking the S3 wire protocol,
/// so the same Minio client family the storage layer uses removes it. Best-effort: cleanup failure must
/// not fail the test, only log.
/// </summary>
internal static class TestBucketCleanup
{
    public static async Task DeleteBlobBucketAsync(
        string endpoint,
        string accessKey,
        string secretKey,
        string bucket,
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

            if (await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken))
            {
                await client.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket), cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Cleanup is best-effort; a failed drop leaks one scratch bucket, it must not fail the run.
            Console.Error.WriteLine($"[TestBucketCleanup] failed to remove bucket '{bucket}': {exception.Message}");
        }
    }
}
