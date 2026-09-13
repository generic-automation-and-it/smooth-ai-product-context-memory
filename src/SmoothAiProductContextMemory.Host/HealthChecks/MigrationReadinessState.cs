namespace SmoothAiProductContextMemory.Host.HealthChecks;

/// <summary>
/// Readiness must mean <em>ready to serve</em>, not <em>process started</em>. Migrations run in a
/// hosted service after Kestrel is listening, so without this latch the application answers requests
/// against a schema that does not exist yet and still looks healthy.
/// </summary>
internal sealed class MigrationReadinessState
{
    private volatile bool _completed;

    internal bool Completed => _completed;

    internal void MarkCompleted() => _completed = true;
}
