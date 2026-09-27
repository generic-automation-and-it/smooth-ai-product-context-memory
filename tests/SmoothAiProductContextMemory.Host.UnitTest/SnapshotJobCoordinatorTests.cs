using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothAiProductContextMemory.Host.Snapshot;

namespace SmoothAiProductContextMemory.Host.UnitTest;

public sealed class SnapshotJobCoordinatorTests
{
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("scope creation failed");
    }

    [Fact]
    public async Task Failing_scope_creation_marks_the_job_failed_not_running()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SmoothAiProductContextMemory"] = "Host=localhost;Database=app",
            })
            .Build();

        var coordinator = new SnapshotJobCoordinator(
            new ThrowingScopeFactory(),
            config,
            NullLogger<SnapshotJobCoordinator>.Instance);

        SnapshotJobState job = await coordinator.StartAsync(TestContext.Current.CancellationToken);
        job.Status.ShouldBe(SnapshotJobStatus.Running);

        // The background task observes the throwing scope factory and must flip the job to Failed
        // rather than faulting unobserved and leaving it Running (the single-slot guard hands that
        // dead job to every later caller until restart).
        Stopwatch sw = Stopwatch.StartNew();
        while (job.Status == SnapshotJobStatus.Running && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        job.Status.ShouldBe(SnapshotJobStatus.Failed);
        job.Error.ShouldBe("Snapshot failed.");
    }
}
