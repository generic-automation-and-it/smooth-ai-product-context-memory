using System.CommandLine;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SmoothAiProductContextMemory.Application.Features.Restore;

namespace SmoothAiProductContextMemory.Host.Cli;

internal static class RestoreCommand
{
    public static async Task<int> InvokeAsync(string[] args)
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

            CliHostResult host = CliHost.Build();
            using (host.Host)
            using (IServiceScope scope = host.Host.Services.CreateScope())
            {
                IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                RestoreArchive.Response response = await mediator.Send(
                    new RestoreArchive.Request(archivePath, host.ConnectionString, force),
                    cancellationToken);

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
