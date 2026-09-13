using Scalar.AspNetCore;
using Serilog;
using SmoothAiProductContextMemory.Application.Extensions;
using SmoothAiProductContextMemory.Host.Cli;
using SmoothAiProductContextMemory.Host.Configuration;
using SmoothAiProductContextMemory.Host.Endpoints;
using SmoothAiProductContextMemory.Infrastructure;

if (args.Length > 0 && string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await ExportCommand.InvokeAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, _, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DatabaseMigrationHostedService>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapScalarApiReference("/scalar/v1");
ContextEndpoints.Map(app);

app.Run();

public partial class Program { }
