using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

internal static class NpgsqlDataSourceFactory
{
    // Defence in depth, not the bound (HLD-003 LADR-07): a statement timeout caps how long any
    // query may run, so a hub-shaped graph cannot pin a backend forever. It reports a timeout rather
    // than an invalid request — the traversal limits are the primary control, this is the long stop.
    private const int CommandTimeoutSeconds = 120;

    /// <summary>
    /// Builds a pooled data source that initialises AGE on every physical connection.
    /// DISCARD ALL on pool return would undo LOAD/search_path, so reset-on-close is off.
    /// </summary>
    /// <param name="connectionString">The connection string, whose <c>Command Timeout</c> is the default.</param>
    /// <param name="commandTimeoutSeconds">
    /// Overrides <see cref="CommandTimeoutSeconds"/>. Capture and restore pass their own budget,
    /// because a bulk statement sized by the corpus can outlast the request-traffic long stop and
    /// <c>SET LOCAL statement_timeout</c> alone does not lift the client-side cap — the client gives
    /// up first and the server keeps holding locks on the only store of record.
    /// </param>
    internal static NpgsqlDataSource Create(string connectionString, int? commandTimeoutSeconds = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.ConnectionStringBuilder.CommandTimeout = commandTimeoutSeconds ?? CommandTimeoutSeconds;
        builder.UsePhysicalConnectionInitializer(AgeSession.Prepare, AgeSession.PrepareAsync);
        return builder.Build();
    }
}
