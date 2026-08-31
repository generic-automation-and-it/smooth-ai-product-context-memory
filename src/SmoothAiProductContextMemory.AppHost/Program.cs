using System.Diagnostics.CodeAnalysis;
using SmoothAiProductContextMemory.AppHost;

[assembly: ExcludeFromCodeCoverage]

var builder = DistributedApplication.CreateBuilder(args);
builder
    .WriteDashboardStartupHint()
    .AddSmoothAiProductContextMemoryAppHostResources()
    .Build()
    .Run();
