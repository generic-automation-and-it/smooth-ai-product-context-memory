using System.CommandLine;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Application.Features.Verify;

namespace SmoothAiProductContextMemory.Host.Cli;

internal static class VerifyCommand
{
    public static async Task<int> InvokeAsync(string[] args)
    {
        var archiveArgument = new Argument<string>("archive")
        {
            Description = "Path to the snapshot archive to verify.",
        };

        var command = new RootCommand("Verify a corpus snapshot archive using only the archive.")
        {
            archiveArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string archivePath = parseResult.GetValue(archiveArgument)!;

            CliHostResult host = CliHost.Build();
            using (host.Host)
            using (IServiceScope scope = host.Host.Services.CreateScope())
            {
                IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                VerifyArchive.Response response = await mediator.Send(
                    new VerifyArchive.Request(archivePath),
                    cancellationToken);

                foreach (SnapshotFinding finding in response.Findings)
                {
                    Console.WriteLine($"  {finding.Kind}: {finding.EntryName} — {finding.Message}");
                }

                Console.WriteLine(
                    response.IsClean
                        ? $"Verify clean: {response.Findings.Count} findings."
                        : $"Verify FAILED: {response.Findings.Count} finding(s).");

                return response.IsClean ? 0 : 1;
            }
        });

        ParseResult parsed = command.Parse(args);
        return await parsed.InvokeAsync();
    }
}
