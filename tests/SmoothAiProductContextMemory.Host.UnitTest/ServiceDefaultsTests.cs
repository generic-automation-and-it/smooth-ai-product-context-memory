extern alias HostApp;

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmoothAiProductContextMemory.Host.HealthChecks;

namespace SmoothAiProductContextMemory.Host.UnitTest;

/// <summary>
/// Boots the Host against unreachable placeholder dependencies. Nothing here needs a container: the
/// point is that the service-defaults registrations resolve and that readiness reports
/// <em>not ready</em> while migrations have not run — the gap that let a dead application look
/// healthy because its dependencies were up.
/// </summary>
public sealed class ServiceDefaultsTests : IAsyncDisposable
{
    private readonly WebApplicationFactory<HostApp::Program> _factory = CreateFactory();

    [Fact]
    public void Open_telemetry_tracing_and_metrics_providers_resolve()
    {
        _factory.Services.GetService<TracerProvider>().ShouldNotBeNull();
        _factory.Services.GetService<MeterProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void Service_discovery_resolves()
    {
        _factory.Services.GetService<ServiceEndpointResolver>().ShouldNotBeNull();
    }

    [Fact]
    public void Http_client_factory_resolves_the_blob_storage_named_client()
    {
        IHttpClientFactory factory = _factory.Services.GetRequiredService<IHttpClientFactory>();

        using HttpClient client = factory.CreateClient(
            SmoothAiProductContextMemory.Infrastructure.Storage.BlobStorageOptions.HttpClientName);

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task Liveness_reports_the_process_responds()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_fails_until_migrations_complete()
    {
        // Every other readiness check is forced healthy, so the 503 can only come from the migration
        // latch — not from the unreachable placeholder database the shared factory points at.
        await using WebApplicationFactory<HostApp::Program> factory = CreateFactory()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    foreach (HealthCheckRegistration registration in options.Registrations
                                 .Where(r => r.Name != "migrations"))
                    {
                        registration.Factory = _ => new HealthyCheck();
                    }
                })));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage pending = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        pending.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        factory.Services.GetRequiredService<MigrationReadinessState>().MarkCompleted();

        using HttpResponseMessage ready = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static WebApplicationFactory<HostApp::Program> CreateFactory() =>
        new WebApplicationFactory<HostApp::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
                builder.UseSetting(
                    "ConnectionStrings:SmoothAiProductContextMemory",
                    "Host=127.0.0.1;Port=1;Database=placeholder;Username=test;Password=test;Timeout=1");
                builder.UseSetting("BlobStorage:Endpoint", "http://127.0.0.1:1");
                builder.UseSetting("BlobStorage:AccessKey", "placeholder");
                builder.UseSetting("BlobStorage:SecretKey", "placeholder");
                builder.UseSetting("BlobStorage:Bucket", "placeholder");
                builder.UseSetting("ApiAccess:ReadToken", "unit-test-read-token");
                builder.UseSetting("ApiAccess:WriteToken", "unit-test-write-token");
            });

    private sealed class HealthyCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }
}
