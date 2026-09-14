using System.Text.Json.Serialization;
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

// Unknown JSON properties are a caller mistake, not data to ignore. A misspelled field (e.g.
// `initiative` where the contract says `initiativeName`) was absorbed silently and the request
// proceeded with a default, so rejection must be explicit and consistent across every endpoint.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSmoothAiProductContextMemoryHealthChecks();
builder.Services.AddHostedService<DatabaseMigrationHostedService>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapScalarApiReference("/scalar/v1");
app.MapDefaultEndpoints();
ContextEndpoints.Map(app);

app.Run();

public partial class Program { }
