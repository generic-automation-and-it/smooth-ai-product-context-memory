using Microsoft.Extensions.Options;

namespace SmoothAiProductContextMemory.Infrastructure.Storage;

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    public string Endpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;
}

public sealed class BlobStorageOptionsValidator : IValidateOptions<BlobStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, BlobStorageOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            failures.Add($"{nameof(BlobStorageOptions.Endpoint)} must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey))
        {
            failures.Add($"{nameof(BlobStorageOptions.AccessKey)} must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            failures.Add($"{nameof(BlobStorageOptions.SecretKey)} must be set.");
        }

        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            failures.Add($"{nameof(BlobStorageOptions.Bucket)} must be set.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
