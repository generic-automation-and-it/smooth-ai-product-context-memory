using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmoothAiProductContextMemory.Application.Extensions;
using SmoothAiProductContextMemory.Infrastructure;

namespace SmoothAiProductContextMemory.Host.Cli;

/// <summary>
/// Shared host builder for the one-shot CLI verbs (snapshot, verify, restore) — the same one path
/// the container entrypoint uses. Builds a plain host (no Kestrel/OpenAPI/migration worker), loads
/// user secrets so the documented local config path works, and composes the same
/// <c>AddApplication</c>/<c>AddInfrastructure</c> the API uses.
/// </summary>
internal static class CliHost
{
    /// <param name="requireConnectionString">Whether a missing connection string fails the build.</param>
    /// <param name="settings">
    /// When supplied, the host's configuration is exactly these values — no appsettings, environment
    /// or user secrets. Lets a test drive a verb without mutating process-wide environment variables
    /// and without a developer's user secrets silently pointing it at a real store.
    /// </param>
    public static CliHostResult Build(
        bool requireConnectionString = true,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = [] });

        if (settings is not null)
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(settings);
        }
        else
        {
            // The CLI host defaults to Production, where user secrets are skipped. Load them explicitly
            // so the documented local config path (user secrets) works for the one-shot verbs too.
            builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
        }

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        string? connectionString = builder.Configuration.GetConnectionString("SmoothAiProductContextMemory");
        if (requireConnectionString && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "A connection string named 'SmoothAiProductContextMemory' is required.");
        }

        return new CliHostResult(builder.Build(), connectionString ?? string.Empty);
    }
}

internal sealed record CliHostResult(IHost Host, string ConnectionString);
