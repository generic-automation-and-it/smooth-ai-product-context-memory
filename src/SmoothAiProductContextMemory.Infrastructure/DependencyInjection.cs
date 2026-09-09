using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Application.Abstractions;
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
        string connectionString = configuration.GetConnectionString("SmoothAiProductContextMemory")
            ?? throw new InvalidOperationException(
                "A connection string named 'SmoothAiProductContextMemory' is required.");

        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(connectionString));

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
