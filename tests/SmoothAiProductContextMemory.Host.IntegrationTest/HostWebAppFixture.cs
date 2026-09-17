extern alias HostApp;

using SmoothAiProductContextMemory.TestFramework.Fixtures;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// Closes the generic <see cref="WebAppFixture{TProgram}"/> over the Host's entry point.
/// The <c>HostApp</c> extern alias disambiguates the Host's <c>Program</c> from the test
/// assembly's own auto-generated <c>Program</c> (xunit.v3 compiles test projects as executables).
/// </summary>
public sealed class HostWebAppFixture : WebAppFixture<HostApp::Program>
{
    public const string ReadToken = "host-integration-read-token";
    public const string WriteToken = "host-integration-write-token";
    private readonly string _databaseName = $"host-integration-{Guid.NewGuid():N}";
    private readonly string _bucket = $"host-int-{Guid.NewGuid():N}";

    protected override string DatabaseName => _databaseName;

    protected override bool RecreateDatabaseOnInitialize => true;

    protected override bool RemoveHostedServices => false;

    protected override Task EnrichConfigurationAsync(Dictionary<string, string?> overrides)
    {
        overrides["ConnectionStrings:SmoothAiProductContextMemory"] = Aspire.CreateDatabaseConnectionString(DatabaseName);
        overrides["BlobStorage:Endpoint"] = Aspire.BlobEndpoint;
        overrides["BlobStorage:AccessKey"] = AspireFixture.BlobAccessKey;
        overrides["BlobStorage:SecretKey"] = AspireFixture.BlobSecretKey;
        overrides["BlobStorage:Bucket"] = _bucket;
        overrides["ApiAccess:ReadToken"] = ReadToken;
        overrides["ApiAccess:WriteToken"] = WriteToken;
        return Task.CompletedTask;
    }

    protected override Task PostInitializeAsync()
    {
        HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", WriteToken);
        return Task.CompletedTask;
    }
}
