using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBlobStorage(configuration);
        services.AddPersistence(configuration);
        return services;
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>((sp, options) =>
        {
            string connectionString = sp.GetRequiredService<IConfiguration>()
                .GetConnectionString("SmoothAiProductContextMemory")
                ?? throw new InvalidOperationException(
                    "A connection string named 'SmoothAiProductContextMemory' is required.");
            options.UseNpgsql(connectionString);
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
