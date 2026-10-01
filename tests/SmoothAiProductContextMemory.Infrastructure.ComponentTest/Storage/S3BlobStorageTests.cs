using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.TestFramework.Fixtures;
using Xunit;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Storage;

/// <summary>
/// L1 — exercises the S3 adapter against the real MinIO container provisioned by <see cref="AspireFixture"/>.
/// Each test uses its own bucket for isolation; the adapter creates the bucket lazily on first write.
/// </summary>
[Collection("Aspire")]
public sealed class S3BlobStorageTests(AspireFixture aspire, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly byte[] SampleContent = Encoding.UTF8.GetBytes("Context memory stores summarised, labelled facts.");

    private readonly List<string> _buckets = [];

    // xUnit v3 does not inject ITestOutputHelper into a collection fixture, so forward the test's
    // helper into the fixture — otherwise the bucket-cleanup report callback is a no-op and a
    // swallowed cleanup failure stays invisible.
    public async ValueTask InitializeAsync()
    {
        aspire.SetOutput(output);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (string bucket in _buckets)
        {
            await BlobBucketCleanup.DeleteBlobBucketAsync(
                aspire.BlobEndpoint,
                AspireFixture.BlobAccessKey,
                AspireFixture.BlobSecretKey,
                bucket,
                report: message => aspire.Output?.WriteLine($"[S3BlobStorageTests] {message}"));
        }
    }


    [Fact]
    public async Task Store_ThenGet_ReturnsSameContent()
    {
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        string address = await storage.StoreAsync(new MemoryStream(SampleContent, writable: false), cancellationToken: ct);

        await using BlobContent? blob = await storage.GetAsync(address, ct);
        blob.ShouldNotBeNull();
        blob.ContentType.ShouldNotBeNullOrWhiteSpace();

        using var reader = new MemoryStream();
        await blob.Content.CopyToAsync(reader, ct);
        reader.ToArray().ShouldBe(SampleContent);
    }

    [Fact]
    public async Task Store_SameContentTwice_ReturnsSameAddress_AndSingleObject()
    {
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        string first = await storage.StoreAsync(new MemoryStream(SampleContent, writable: false), cancellationToken: ct);
        string second = await storage.StoreAsync(new MemoryStream(SampleContent, writable: false), cancellationToken: ct);

        first.ShouldBe(second);
        (await storage.ExistsAsync(first, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Get_MissingAddress_ReturnsNull()
    {
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using BlobContent? blob = await storage.GetAsync("aa/bb/cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", ct);

        blob.ShouldBeNull();
    }

    [Fact]
    public async Task Exists_IsFalse_ForMissingAddress_AndTrue_AfterStore()
    {
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        string address = await storage.StoreAsync(new MemoryStream(SampleContent, writable: false), cancellationToken: ct);

        (await storage.ExistsAsync(address, ct)).ShouldBeTrue();
        (await storage.ExistsAsync("bb/cc/dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_RemovesObject()
    {
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        string address = await storage.StoreAsync(new MemoryStream(SampleContent, writable: false), cancellationToken: ct);
        await storage.DeleteAsync(address, ct);

        (await storage.ExistsAsync(address, ct)).ShouldBeFalse();
        (await storage.GetAsync(address, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Store_LargeContent_CompressesAndRoundTrips()
    {
        byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("factual claim about the system\n", 500)));
        await using var storage = CreateIsolatedStorage();
        CancellationToken ct = TestContext.Current.CancellationToken;

        string address = await storage.StoreAsync(new MemoryStream(large, writable: false), cancellationToken: ct);

        await using BlobContent? blob = await storage.GetAsync(address, ct);
        blob.ShouldNotBeNull();

        using var reader = new MemoryStream();
        await blob.Content.CopyToAsync(reader, ct);
        reader.ToArray().ShouldBe(large);
    }

    private S3BlobStorage CreateIsolatedStorage()
    {
        string bucket = $"s3blob-{Guid.NewGuid():N}";
        _buckets.Add(bucket);

        var options = Options.Create(new BlobStorageOptions
        {
            Endpoint = aspire.BlobEndpoint,
            AccessKey = AspireFixture.BlobAccessKey,
            SecretKey = AspireFixture.BlobSecretKey,
            Bucket = bucket,
        });

        return new S3BlobStorage(options, new TestHttpClientFactory(), NullLogger<S3BlobStorage>.Instance);
    }
}
