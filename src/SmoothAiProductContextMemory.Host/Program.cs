using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using SmoothAiProductContextMemory.Application.Extensions;
using SmoothAiProductContextMemory.Host.Cli;
using SmoothAiProductContextMemory.Host.Configuration;
using SmoothAiProductContextMemory.Host.Endpoints;
using SmoothAiProductContextMemory.Host.HealthChecks;
using SmoothAiProductContextMemory.Infrastructure;

if (args.Length > 0 && string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await ExportCommand.InvokeAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseConfiguredSerilog();
builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOptions<ApiAccessOptions>()
    .Bind(builder.Configuration.GetSection(ApiAccessOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ReadToken)
            && !string.IsNullOrWhiteSpace(options.WriteToken),
        "ApiAccess read and write tokens are required.")
    .Validate(
        options => !string.Equals(options.ReadToken, options.WriteToken, StringComparison.Ordinal),
        "ApiAccess read and write tokens must be distinct.")
    .ValidateOnStart();
builder.Services.AddSingleton<ApiAccessAuthorizer>();

// Unknown JSON properties are a caller mistake, not data to ignore. A misspelled field (e.g.
// `initiative` where the contract says `initiativeName`) was absorbed silently and the request
// proceeded with a default, so rejection must be explicit and consistent across every endpoint.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddOpenApi(options =>
{
    options.CreateSchemaReferenceId = type => type.Type.FullName?.Replace('+', '.');
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "opaque",
            Description = "Runtime read or write capability token. Write token includes read access.",
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        RequiredApiCapability? capability = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequiredApiCapability>()
            .SingleOrDefault();
        if (capability is not null)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
            });
            operation.Description = $"Requires {capability.Value.ToString().ToLowerInvariant()} capability.";
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSmoothAiProductContextMemoryHealthChecks();
builder.Services.AddHostedService<DatabaseMigrationHostedService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseApiAccessAuthorization();
app.MapOpenApi();
app.MapScalarApiReference("/scalar/v1");
app.MapDefaultEndpoints();
ContextEndpoints.Map(app);

app.Run();

public partial class Program { }
