using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using SmoothAiProductContextMemory.AppHost;

[assembly: ExcludeFromCodeCoverage]

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DashboardApplicationName = "Mímisbrunnr",
});
builder
    .WriteDashboardStartupHint()
    .AddSmoothAiProductContextMemoryAppHostResources()
    .Build()
    .Run();
