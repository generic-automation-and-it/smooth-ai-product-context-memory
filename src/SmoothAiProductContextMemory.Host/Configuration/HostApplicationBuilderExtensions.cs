using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SmoothAiProductContextMemory.Host.Configuration;

/// <summary>
/// Aspire service defaults, deliberately inlined here rather than living in a shared
/// <c>ServiceDefaults</c> project — the surface is too small to justify a library, and the sibling
/// repository already tried the separate project and reversed it. Recorded in <c>HOST_AGENTS.md</c>.
/// </summary>
internal static class HostApplicationBuilderExtensions
{
    private const string NpgsqlTelemetryName = "Npgsql";
    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    internal static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Serilog owns console and writeToProviders forwards to whatever MEL providers
        // remain. The default builder registers a console provider of its own, so leaving it in place
        // prints every record twice — the "duplicate records in one surface" failure this wiring is
        // meant to avoid. After clearing, OpenTelemetry is the only provider and the only extra
        // destination is OTLP.
        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(NpgsqlTelemetryName))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(new ConfidentialityTraceProcessor())
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(NpgsqlTelemetryName)
                .AddAspNetCoreInstrumentation(options => options.RecordException = false)
                .AddHttpClientInstrumentation(options => options.RecordException = false));

        builder.AddOpenTelemetryExporters();
    }

    private static void AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }
}
