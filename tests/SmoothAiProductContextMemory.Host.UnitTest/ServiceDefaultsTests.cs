extern alias HostApp;

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
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
            });
}
