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
        // Read the connection string before publishing any job state, so a missing configuration
        // throws rather than leaving a phantom `Running` job behind.
        string connectionString = ConnectionString;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            // Single-slot invariant: refuse a second trigger while one snapshot is still running,
            // returning the in-flight job so the caller polls the one that is actually running.
            if (_latest is { Status: SnapshotJobStatus.Running } running)
            {
                return running;
            }

            var job = new SnapshotJobState { Id = Guid.NewGuid(), Status = SnapshotJobStatus.Running };
            job.DestinationPath = Path.Combine(DestinationDirectory, $"snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.tar");
            _latest = job;

            _ = Task.Run(async () =>
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<Mediator.IMediator>();
                try
                {
                    SnapshotStore.Response response = await mediator.Send(
                        new SnapshotStore.Request(connectionString, job.DestinationPath),
                        CancellationToken.None);

                    // Populate every result field before flipping Status to Completed, so a poller
                    // reading until Status == Completed can never observe it with null/zero payload.
                    job.ResultPath = response.DestinationPath;
                    job.Memories = response.Memories;
                    job.Versions = response.Versions;
                    job.Vertices = response.Vertices;
                    job.Edges = response.Edges;
                    job.Objects = response.Objects;
                    job.DanglingReferences = response.DanglingReferences;
                    job.UnreferencedObjects = response.UnreferencedObjects;
                    job.MismatchedBodies = response.MismatchedBodies;
                    job.Status = SnapshotJobStatus.Completed;
                    Volatile.Write(ref _latest, job);
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
        finally
        {
            _mutex.Release();
        }
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

    public int MismatchedBodies { get; set; }
}

public enum SnapshotJobStatus
{
    Running,
    Completed,
    Failed,
}
