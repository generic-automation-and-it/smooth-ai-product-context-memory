using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Export;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBlobStorage(configuration);
        services.AddPersistence(configuration);
        services.AddScoped<IMarkdownExportSink, FileSystemMarkdownExportSink>();
        return services;
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // One shared data source for ALL relational and graph connections. NoResetOnClose (set in
        // NpgsqlDataSourceFactory) means session state is never reset on pool return — required to
        // keep AGE LOAD/search_path, but it also means a leaked session-scoped `SET` (or temp
        // table/advisory lock) survives into the next borrower. The append-only bypass GUC must
        // therefore always be `SET LOCAL` inside a transaction — see PERSISTENCE_AGENTS.md.
        services.AddSingleton(sp =>
        {
            string connectionString = sp.GetRequiredService<IConfiguration>()
                .GetConnectionString("SmoothAiProductContextMemory")
                ?? throw new InvalidOperationException(
                    "A connection string named 'SmoothAiProductContextMemory' is required.");
            return NpgsqlDataSourceFactory.Create(connectionString);
        });

        services.AddDbContext<SmoothAiProductContextMemoryDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
        });
        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<SmoothAiProductContextMemoryDbContext>());

        // Retrieval and error translation are provider-specific: matching the full-text and array
        // indexes needs Npgsql operators, and constraint identity is a SQLSTATE. Application depends
        // on the abstractions only.
        services.AddScoped<IMemorySearch, NpgsqlMemorySearch>();
        services.AddSingleton<IDbErrorMapper, NpgsqlDbErrorMapper>();

        return services;
    }

    private static IServiceCollection AddBlobStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<BlobStorageOptions>()
            .Bind(configuration.GetSection(BlobStorageOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<BlobStorageOptions>, BlobStorageOptionsValidator>();
        services.AddSingleton<IBlobStorage, S3BlobStorage>();

        return services;
    }
}
