extern alias HostApp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using SmoothAiProductContextMemory.TestFramework.Fixtures;
using SmoothAiProductContextMemory.TestFramework.Logging;
using SmoothAiProductContextMemory.TestFramework.Telemetry;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// Host fixture with the telemetry pipeline observable: a span processor appended after the
/// application's own, a meter listener, and a capturing <c>ILoggerProvider</c>. Keeps the migration
/// hosted service so readiness can be asserted as genuinely ready rather than merely started.
/// </summary>
public sealed class ObservabilityWebAppFixture : WebAppFixture<HostApp::Program>
{
    private readonly string _databaseName = $"host-integration-{Guid.NewGuid():N}";
    private readonly string _bucket = $"host-obs-{Guid.NewGuid():N}";

    public TelemetryCapture Telemetry { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    protected override string DatabaseName => _databaseName;

    protected override bool RecreateDatabaseOnInitialize => true;

    protected override bool RemoveHostedServices => false;

    /// <summary>Every string the application emitted to logs, span attributes or metric tags.</summary>
    public IEnumerable<string> AllTelemetryText() => Telemetry.AllTelemetryText().Concat(Logs.Records);

    protected override Task EnrichConfigurationAsync(Dictionary<string, string?> overrides)
    {
        overrides["ConnectionStrings:SmoothAiProductContextMemory"] = Aspire.CreateDatabaseConnectionString(DatabaseName);
        overrides["BlobStorage:Endpoint"] = Aspire.BlobEndpoint;
        overrides["BlobStorage:AccessKey"] = AspireFixture.BlobAccessKey;
        overrides["BlobStorage:SecretKey"] = AspireFixture.BlobSecretKey;
        overrides["BlobStorage:Bucket"] = _bucket;
        overrides["ApiAccess:ReadToken"] = HostWebAppFixture.ReadToken;
        overrides["ApiAccess:WriteToken"] = HostWebAppFixture.WriteToken;
        return Task.CompletedTask;
    }

    protected override Task PostInitializeAsync()
    {
        HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostWebAppFixture.WriteToken);
        return Task.CompletedTask;
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(Logs);
        services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddProcessor(Telemetry.CreateSpanProcessor()));
    }
}
