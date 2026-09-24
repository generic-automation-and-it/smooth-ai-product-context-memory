using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Features.Snapshot;

namespace SmoothAiProductContextMemory.Host.Snapshot;

/// <summary>
/// Runs a snapshot as a background job so the HTTP trigger can be accepted-then-poll rather than
/// holding a connection open for the full corpus walk (LADR-04 / LADR-07). A single-user local store
/// needs at most one in-flight snapshot, so a single latest-job slot suffices. Content-free: the
/// state carries counts and a result path, never memory content.
/// </summary>
public sealed class SnapshotJobCoordinator(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SnapshotJobCoordinator> logger)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SnapshotJobState? _latest;

    private string ConnectionString => configuration.GetConnectionString("SmoothAiProductContextMemory")
        ?? throw new InvalidOperationException("A connection string named 'SmoothAiProductContextMemory' is required.");

    private string DestinationDirectory => configuration["Snapshot:DestinationDirectory"] ?? Path.Combine(".context", "snapshots");

    public SnapshotJobState? Latest => Volatile.Read(ref _latest);

    public async Task<SnapshotJobState> StartAsync(CancellationToken cancellationToken)
    {
        var job = new SnapshotJobState { Id = Guid.NewGuid(), Status = SnapshotJobStatus.Running };
        string destination = Path.Combine(DestinationDirectory, $"snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N[..8]}.tar");
        job.DestinationPath = destination;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            _latest = job;
        }
        finally
        {
            _mutex.Release();
        }

        string connectionString = ConnectionString;

        _ = Task.Run(async () =>
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<Mediator.IMediator>();
            try
            {
                SnapshotStore.Response response = await mediator.Send(
                    new SnapshotStore.Request(connectionString, destination),
                    CancellationToken.None);

                job.Status = SnapshotJobStatus.Completed;
                job.ResultPath = response.DestinationPath;
                job.Memories = response.Memories;
                job.Versions = response.Versions;
                job.Vertices = response.Vertices;
                job.Edges = response.Edges;
                job.Objects = response.Objects;
                job.DanglingReferences = response.DanglingReferences;
                job.UnreferencedObjects = response.UnreferencedObjects;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Snapshot job failed");
                job.Status = SnapshotJobStatus.Failed;
                job.Error = "Snapshot failed.";
            }
        });

        return job;
    }
}

public sealed class SnapshotJobState
{
    public Guid Id { get; set; }

    public SnapshotJobStatus Status { get; set; }

    public string? ResultPath { get; set; }

    public string? DestinationPath { get; set; }

    public string? Error { get; set; }

    public int Memories { get; set; }

    public int Versions { get; set; }

    public int Vertices { get; set; }

    public int Edges { get; set; }

    public int Objects { get; set; }

    public int DanglingReferences { get; set; }

    public int UnreferencedObjects { get; set; }
}

public enum SnapshotJobStatus
{
    Running,
    Completed,
    Failed,
}
