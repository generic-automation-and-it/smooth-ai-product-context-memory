using SmoothAiProductContextMemory.Knowledge;

namespace SmoothAiProductContextMemory.KnowledgeHost;

public sealed class CaptureWorker(CaptureJournal journal, Workflow workflow, ILogger<CaptureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var id in journal.Pending())
            {
                logger.LogInformation("Capture processing started {CaptureId}", id);
                await workflow.ProcessAsync(id, stoppingToken);
                logger.LogInformation("Capture processing completed {CaptureId}", id);
            }
            await journal.PurgeAsync(TimeSpan.FromDays(7), stoppingToken);
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
