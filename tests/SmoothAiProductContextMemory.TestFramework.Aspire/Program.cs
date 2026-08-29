using System.Diagnostics.CodeAnalysis;
using SmoothAiProductContextMemory.TestFramework.Aspire;

[assembly: ExcludeFromCodeCoverage]

var builder = DistributedApplication.CreateBuilder(args);
builder.AddSmoothAiProductContextMemoryTestDependencies();

builder.Build().Run();
