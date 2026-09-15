using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SmoothAiProductContextMemory.AppHost;

[assembly: ExcludeFromCodeCoverage]

if (args is ["--validate-configuration"])
{
    try
    {
        AppHostConfiguration configuration = AppHostConfiguration.Create(new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build());
        if (!configuration.IsRelease)
        {
            throw new InvalidOperationException("Configuration validation requires release mode.");
        }

        Console.WriteLine("Release configuration is valid.");
        return;
    }
    catch (Exception exception) when (exception is InvalidOperationException or FormatException)
    {
        Console.Error.WriteLine($"Invalid AppHost configuration: {exception.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DashboardApplicationName = "Mímisbrunnr",
});

if (string.Equals(
    builder.Configuration["AppHostConfiguration:Mode"],
    nameof(AppHostMode.Release),
    StringComparison.OrdinalIgnoreCase))
{
    builder.Services
        .AddDataProtection()
        .SetApplicationName("SmoothAiProductContextMemory.AppHost")
        .PersistKeysToFileSystem(new DirectoryInfo("/var/lib/mimisbrunnr/data-protection"));
}

builder
    .WriteDashboardStartupHint()
    .AddSmoothAiProductContextMemoryAppHostResources()
    .Build()
    .Run();
