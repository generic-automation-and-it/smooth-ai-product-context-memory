using System.CommandLine;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmoothAiProductContextMemory.Application.Extensions;
using SmoothAiProductContextMemory.Application.Features.Export;
using SmoothAiProductContextMemory.Infrastructure;

namespace SmoothAiProductContextMemory.Host.Cli;

internal static class ExportCommand
{
    public static async Task<int> InvokeAsync(string[] args)
    {
        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write the generated Markdown tree. Default: .context/export",
            DefaultValueFactory = _ => Path.Combine(".context", "export"),
        };
        var historyOption = new Option<bool>("--history")
        {
            Description = "Include version chains. Default is current-only.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Wipe an unmarked non-empty output directory.",
        };

        var command = new RootCommand("Render the context-memory store as a generated Markdown tree.")
        {
            outputOption,
            historyOption,
            forceOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string output = parseResult.GetValue(outputOption) ?? Path.Combine(".context", "export");
            bool history = parseResult.GetValue(historyOption);
            bool force = parseResult.GetValue(forceOption);

            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { Args = [] });

            // The CLI host defaults to Production, where user secrets are skipped. Load them
            // explicitly so the documented local config path (user secrets) works for export.
            builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);

            using IHost host = builder.Build();
            using IServiceScope scope = host.Services.CreateScope();
            IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            ExportStore.Response response = await mediator.Send(
                new ExportStore.Request(output, history, force),
                cancellationToken);

            Console.WriteLine(
                $"Export wrote {response.FilesWritten} files ({response.Groups} groups, {response.Memories} memories, {response.MissingBlobs} missing blobs, {response.NonTextBlobs} non-text blobs).");
        });

        ParseResult parsed = command.Parse(args);
        return await parsed.InvokeAsync();
    }
}
