using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

internal static class NpgsqlDataSourceFactory
{
    /// <summary>
    /// Builds a pooled data source that initialises AGE on every physical connection.
    /// DISCARD ALL on pool return would undo LOAD/search_path, so reset-on-close is off.
    /// </summary>
    internal static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.UsePhysicalConnectionInitializer(AgeSession.Prepare, AgeSession.PrepareAsync);
        return builder.Build();
    }
}
