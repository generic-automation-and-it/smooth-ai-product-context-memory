using System.CommandLine;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmoothAiProductContextMemory.Application.Features.Snapshot;

namespace SmoothAiProductContextMemory.Host.Cli;

internal static class SnapshotCommand
{
    public static async Task<int> InvokeAsync(string[] args)
    {
        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write the snapshot archive. Default: .context/snapshots",
            DefaultValueFactory = _ => Path.Combine(".context", "snapshots"),
        };

        var command = new RootCommand("Capture both stores into one self-verifying corpus snapshot archive.")
        {
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string output = parseResult.GetValue(outputOption) ?? Path.Combine(".context", "snapshots");
            string destination = Path.Combine(output, $"snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.tar");

            CliHostResult host = CliHost.Build();
            using (host.Host)
            using (IServiceScope scope = host.Host.Services.CreateScope())
            {
                IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                SnapshotStore.Response response = await mediator.Send(
                    new SnapshotStore.Request(host.ConnectionString, destination),
                    cancellationToken);

                Console.WriteLine(
                    $"Snapshot wrote {response.DestinationPath} ({response.Memories} memories, {response.Versions} versions, {response.Vertices} vertices, {response.Edges} edges).");
                Console.WriteLine(
                    $"dangling={response.DanglingReferences} unreferenced={response.UnreferencedObjects} mismatched={response.MismatchedBodies}");

                // A snapshot with dangling references or mismatched bodies is not complete; exit
                // non-zero so a script can gate on it rather than treating a degraded archive as clean.
                return response.DanglingReferences != 0 || response.MismatchedBodies != 0 ? 1 : 0;
            }
        });

        ParseResult parsed = command.Parse(args);
        return await parsed.InvokeAsync();
    }
}
