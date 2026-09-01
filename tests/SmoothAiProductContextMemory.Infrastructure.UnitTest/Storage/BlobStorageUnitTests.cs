using System.Text;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Infrastructure.UnitTest.Storage;

public class BlobStorageUnitTests
{
    [Fact]
    public void ContentAddress_IsDeterministic_ForIdenticalContent()
    {
        byte[] content = Encoding.UTF8.GetBytes("PostgreSQL is the storage engine.");

        string first = Sha256ContentAddress.Compute(content);
        string second = Sha256ContentAddress.Compute(content);

        first.ShouldBe(second);
    }

    [Fact]
    public void ContentAddress_Differs_ForDifferentContent()
    {
        byte[] a = Encoding.UTF8.GetBytes("PostgreSQL is the storage engine.");
        byte[] b = Encoding.UTF8.GetBytes("Postgres is the storage engine.");

        Sha256ContentAddress.Compute(a).ShouldNotBe(Sha256ContentAddress.Compute(b));
    }

    [Fact]
    public void ContentAddress_IsShardedHex_Lowercase()
    {
        byte[] content = Encoding.UTF8.GetBytes("refactored context memory");

        string address = Sha256ContentAddress.Compute(content);

        address.ShouldMatch("^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}$");
    }

    [Fact]
    public void Compressor_RoundTrips_Content()
    {
        byte[] original = Encoding.UTF8.GetBytes("".PadRight(10_000, 'a'));
        byte[] compressed = BlobCompressor.Compress(new MemoryStream(original, writable: false));

        using Stream decompressed = BlobCompressor.Decompress(compressed);
        using var result = new MemoryStream();
        decompressed.CopyTo(result);

        result.ToArray().ShouldBe(original);
    }

    [Fact]
    public void Compressor_Shrinks_CompressibleContent()
    {
        byte[] original = Encoding.UTF8.GetBytes("".PadRight(10_000, 'a'));
        byte[] compressed = BlobCompressor.Compress(new MemoryStream(original, writable: false));

        compressed.Length.ShouldBeLessThan(original.Length);
    }

    [Fact]
    public void Compressor_EncodingName_IsGzip()
    {
        BlobCompressor.EncodingName.ShouldBe("gzip");
    }

    [Theory]
    [InlineData("", "k", "s", "b")]
    [InlineData("e", "", "s", "b")]
    [InlineData("e", "k", "", "b")]
    [InlineData("e", "k", "s", "")]
    public void OptionsValidator_Fails_WhenFieldMissing(string endpoint, string key, string secret, string bucket)
    {
        BlobStorageOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            nameof(BlobStorageOptions),
            new BlobStorageOptions
            {
                Endpoint = endpoint,
                AccessKey = key,
                SecretKey = secret,
                Bucket = bucket,
            });

        result.Succeeded.ShouldBeFalse();
        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void OptionsValidator_Succeeds_WhenAllFieldsSet()
    {
        BlobStorageOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            nameof(BlobStorageOptions),
            new BlobStorageOptions
            {
                Endpoint = "http://localhost:9000",
                AccessKey = "access",
                SecretKey = "secret",
                Bucket = "bucket",
            });

        result.Succeeded.ShouldBeTrue();
    }
}
