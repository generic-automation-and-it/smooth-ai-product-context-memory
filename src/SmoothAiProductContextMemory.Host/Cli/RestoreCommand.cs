using System.CommandLine;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SmoothAiProductContextMemory.Application.Common.Exceptions;
using SmoothAiProductContextMemory.Application.Features.Restore;

namespace SmoothAiProductContextMemory.Host.Cli;

internal static class RestoreCommand
{
    public static Task<int> InvokeAsync(string[] args) => InvokeAsync(args, settings: null);

    /// <summary>Same verb with its configuration supplied in full; see <see cref="CliHost.Build"/>.</summary>
    internal static async Task<int> InvokeAsync(string[] args, IReadOnlyDictionary<string, string?>? settings)
    {
        var archiveArgument = new Argument<string>("archive")
        {
            Description = "Path to the snapshot archive to restore.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Allow restore into a non-empty target (explicit override).",
        };

        var command = new RootCommand("Restore both stores from a snapshot archive, then print a reconciliation.")
        {
            archiveArgument,
            forceOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string archivePath = parseResult.GetValue(archiveArgument)!;
            bool force = parseResult.GetValue(forceOption);

            CliHostResult host = CliHost.Build(settings: settings);
            using (host.Host)
            using (IServiceScope scope = host.Host.Services.CreateScope())
            {
                IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                RestoreArchive.Response response;
                try
                {
                    response = await mediator.Send(
                        new RestoreArchive.Request(archivePath, host.ConnectionString, force),
                        cancellationToken);
                }
                catch (ArchiveVerificationFailedException ex)
                {
                    // Integrity failure — the archive itself is bad, distinct from an operational
                    // failure (exit 2 vs exit 1) so an operator script can tell them apart. Print
                    // the findings the way verify does; nothing was mutated.
                    CliFindingRenderer.PrintFindings(ex.Findings);
                    Console.WriteLine($"Restore refused: {ex.Message}");
                    return 2;
                }

                foreach (RestoreArchive.ReconciliationLine line in response.Lines)
                {
                    Console.WriteLine($"  {(line.Matches ? "OK " : "FAIL")}  {line.Statement}");
                }

                Console.WriteLine(
                    response.Reconciled
                        ? "Restore reconciled: arithmetic closes."
                        : "Restore reconciliation FAILED.");

                return response.Reconciled ? 0 : 1;
            }
        });

        ParseResult parsed = command.Parse(args);
        return await parsed.InvokeAsync();
    }
}
