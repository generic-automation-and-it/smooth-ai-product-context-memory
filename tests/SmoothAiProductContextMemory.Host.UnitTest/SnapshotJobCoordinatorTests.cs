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

    /// <summary>A scope factory that blocks until released, so a job can be held genuinely in flight.</summary>
    private sealed class BlockingScopeFactory : IServiceScopeFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public IServiceScope CreateScope()
        {
            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("released");
        }
    }

    private static IConfiguration Configured() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmoothAiProductContextMemory"] = "Host=localhost;Database=app",
        })
        .Build();

    [Fact]
    public async Task Second_trigger_while_running_returns_the_in_flight_job_rather_than_starting_another()
    {
        var factory = new BlockingScopeFactory();
        var coordinator = new SnapshotJobCoordinator(
            factory, Configured(), NullLogger<SnapshotJobCoordinator>.Instance);

        try
        {
            SnapshotJobState first = await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await factory.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            SnapshotJobState second = await coordinator.StartAsync(TestContext.Current.CancellationToken);

            // Same job, not a new one. Starting a second snapshot would walk the corpus concurrently
            // with the first and race it for the same destination; the caller is expected to poll the
            // one that is actually running.
            second.Id.ShouldBe(first.Id);
            coordinator.Latest!.Id.ShouldBe(first.Id);
            second.Status.ShouldBe(SnapshotJobStatus.Running);
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task A_finished_job_does_not_block_the_next_trigger()
    {
        var coordinator = new SnapshotJobCoordinator(
            new ThrowingScopeFactory(), Configured(), NullLogger<SnapshotJobCoordinator>.Instance);

        SnapshotJobState first = await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await SettleAsync(first);

        SnapshotJobState second = await coordinator.StartAsync(TestContext.Current.CancellationToken);

        // The guard keys on Running, not on "a job exists" — otherwise one snapshot per process
        // lifetime would be the ceiling.
        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public void Missing_connection_string_throws_without_publishing_a_job()
    {
        IConfiguration empty = new ConfigurationBuilder().Build();
        var coordinator = new SnapshotJobCoordinator(
            new ThrowingScopeFactory(), empty, NullLogger<SnapshotJobCoordinator>.Instance);

        Should.Throw<InvalidOperationException>(
            () => coordinator.StartAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        // Read before the slot is written, so a misconfiguration cannot leave a phantom Running job
        // that the single-slot guard would then hand to every later caller.
        coordinator.Latest.ShouldBeNull();
    }

    private static async Task SettleAsync(SnapshotJobState job)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (job.Status == SnapshotJobStatus.Running && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
