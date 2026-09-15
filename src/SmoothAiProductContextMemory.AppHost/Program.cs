using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using SmoothAiProductContextMemory.AppHost;

[assembly: ExcludeFromCodeCoverage]

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
