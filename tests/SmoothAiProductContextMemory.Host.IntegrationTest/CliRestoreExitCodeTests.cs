extern alias HostApp;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Infrastructure.Persistence;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Extensions;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;
using SmoothAiProductContextMemory.TestFramework.Fixtures;
using RestoreCommand = HostApp::SmoothAiProductContextMemory.Host.Cli.RestoreCommand;

namespace SmoothAiProductContextMemory.Host.IntegrationTest;

/// <summary>
/// The <c>restore</c> verb's two outcomes that need a real database: 0 when the reconciliation closes
/// and 1 when it does not. The integrity code (2) is pinned without a database in the unit tests.
/// </summary>
/// <remarks>
/// Runs in a non-parallel collection because the verb prints its verdict to the process-wide
/// <see cref="Console.Out"/>, and asserting that verdict is what tells a failed reconciliation apart
/// from an operational crash — both exit 1.
/// </remarks>
[Collection(CliRestoreCollection.Name)]
public sealed class CliRestoreExitCodeTests(AspireFixture aspire)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restore_exits_zero_when_the_reconciliation_closes()
    {
        Guid source = Guid.NewGuid();
        Guid target = Guid.NewGuid();
        var capture = Graph([source, target], [new SnapshotEdge(source, target, "relates_to", "reason")]);

        (int exit, string printed, long edges) = await RestoreAsync(capture);

        exit.ShouldBe(0, printed);
        printed.ShouldContain("Restore reconciled: arithmetic closes.");
        edges.ShouldBe(1);
    }

    [Fact]
    public async Task Restore_exits_one_when_the_reconciliation_does_not_close()
    {
        // The archive verifies clean — verify reconciles counts against the members, not graph
        // endpoints — but the edge names vertices the capture does not hold, so its CREATE matches
        // nothing and only the read-back sees the loss.
        var capture = Graph([Guid.NewGuid(), Guid.NewGuid()], [new SnapshotEdge(Guid.NewGuid(), Guid.NewGuid(), "relates_to", "reason")]);

        (int exit, string printed, long edges) = await RestoreAsync(capture);

        exit.ShouldBe(1, printed);
        printed.ShouldContain("Restore reconciliation FAILED.");
        printed.ShouldContain("committed      no — rolled back");
        edges.ShouldBe(0);
    }

    private static SnapshotCapture Graph(Guid[] vertices, SnapshotEdge[] edges) =>
        new([], [], [], [], [], [], [.. vertices.Select(v => new SnapshotVertex(v))], edges, [], []);

    private async Task<(int Exit, string Printed, long Edges)> RestoreAsync(SnapshotCapture capture)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cli-restore-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(directory, "snapshot.tar");
        await using SmoothAiProductContextMemoryTestDatabase database =
            await SmoothAiProductContextMemoryTestDatabase.CreateAsync(aspire, $"cli-restore-{Guid.NewGuid():N}", Ct);
        try
        {
            await MigrateAsync(database.ConnectionString);
            await new TarSnapshotArchive().WriteAsync(
                archivePath,
                capture,
                new SnapshotWalkResult([], 0, 0),
                (_, _) => throw new InvalidOperationException("the capture cites no body"),
                Ct);

            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmoothAiProductContextMemory"] = database.ConnectionString,
                ["BlobStorage:Endpoint"] = aspire.BlobEndpoint,
                ["BlobStorage:AccessKey"] = AspireFixture.BlobAccessKey,
                ["BlobStorage:SecretKey"] = AspireFixture.BlobSecretKey,
                // Never created: the capture cites no body, so restore stores and reads nothing.
                ["BlobStorage:Bucket"] = $"cli-restore-{Guid.NewGuid():N}",
            };

            var stdout = new StringWriter();
            TextWriter previous = Console.Out;
            Console.SetOut(stdout);
            int exit;
            try
            {
                exit = await RestoreCommand.InvokeAsync([archivePath], settings);
            }
            finally
            {
                Console.SetOut(previous);
            }

            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM memory_graph.\"LINKS\"", connection);
            long edges = Convert.ToInt64(await count.ExecuteScalarAsync(Ct));
            return (exit, stdout.ToString(), edges);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<SmoothAiProductContextMemoryDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.MigrateSmoothAiProductContextMemoryAsync(Ct);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliRestoreCollection : ICollectionFixture<AspireFixture>
{
    public const string Name = "CLI restore against a real store";
}
