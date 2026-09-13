using Microsoft.Extensions.Configuration;
using Serilog;

namespace SmoothAiProductContextMemory.Host.Configuration;

/// <summary>
/// Serilog is the single authoritative logging pipeline (recorded in <c>HOST_AGENTS.md</c>).
/// <c>writeToProviders: true</c>
/// forwards every record to the registered <c>ILoggerProvider</c>s as well, so the OpenTelemetry
/// provider exports the same events over OTLP. Console and Seq are Serilog sinks; the dashboard is a
/// provider. No destination is served twice.
/// </summary>
/// <remarks>
/// <c>preserveStaticLogger: true</c> keeps the pipeline owned by this host rather than by the global
/// <c>Log.Logger</c>. Without it Serilog registers its factory against the static logger, so a second
/// host booted in the same process — which is exactly what L2 does — silently redirects the first
/// host's records to the second host's sinks and providers.
/// </remarks>
internal static class LoggingConfigurationExtensions
{
    private const string SerilogSectionRoot = "Logging";
    private const string SeqConnectionStringName = "seq";
    private const string SeqUriKey = "SEQ_URI";

    internal static IHostBuilder UseConfiguredSerilog(this IHostBuilder host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.UseSerilog(
            (context, loggerConfiguration) =>
            {
                loggerConfiguration.ReadFrom.Configuration(context.Configuration.GetSection(SerilogSectionRoot));

                string? seqUri = context.Configuration.ResolveSeqUri();
                if (!string.IsNullOrWhiteSpace(seqUri))
                {
                    loggerConfiguration.WriteTo.Async(writeTo => writeTo.Seq(seqUri));
                }
            },
            preserveStaticLogger: true,
            writeToProviders: true);
    }

    /// <summary>
    /// The dev AppHost injects <c>ConnectionStrings:seq</c> via <c>WithReference(seq)</c> —
    /// <c>Aspire.Hosting.Seq</c> publishes no <c>SEQ_URI</c> variable, so reading only that key is
    /// why the Seq container has always been empty. <c>SEQ_URI</c> stays honoured as an override.
    /// </summary>
    internal static string? ResolveSeqUri(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? explicitUri = configuration[SeqUriKey];
        return string.IsNullOrWhiteSpace(explicitUri)
            ? configuration.GetConnectionString(SeqConnectionStringName)
            : explicitUri;
    }
}
