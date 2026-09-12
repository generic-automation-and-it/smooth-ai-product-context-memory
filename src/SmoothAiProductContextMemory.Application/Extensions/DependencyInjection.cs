using FluentValidation;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SmoothAiProductContextMemory.Application.Common.Pipelines;

namespace SmoothAiProductContextMemory.Application.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.Assemblies = [typeof(DependencyInjection).Assembly];
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>)];
        });

        return services;
    }
}
