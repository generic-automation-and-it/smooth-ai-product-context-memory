using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBlobStorage(configuration);
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
