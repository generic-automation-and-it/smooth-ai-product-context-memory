using Npgsql;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// AGE session preparation. LOAD and search_path are per physical connection, not per database —
/// a correctly installed extension still fails on an unprepared pooled connection (LADR-04).
/// </summary>
internal static class AgeSession
{
    internal const string GraphName = "memory_graph";
    internal const string VertexLabel = "Memory";

    internal const string LoadLibrarySql = "LOAD 'age';";
    internal const string SearchPathSql = """SET search_path = ag_catalog, "$user", public;""";

    internal static void Prepare(NpgsqlConnection connection)
    {
        if (!ExtensionInstalled(connection))
        {
            return;
        }

        using var load = new NpgsqlCommand(LoadLibrarySql, connection);
        load.ExecuteNonQuery();
        using var searchPath = new NpgsqlCommand(SearchPathSql, connection);
        searchPath.ExecuteNonQuery();
    }

    internal static async Task PrepareAsync(NpgsqlConnection connection)
    {
        if (!await ExtensionInstalledAsync(connection))
        {
            return;
        }

        await using var load = new NpgsqlCommand(LoadLibrarySql, connection);
        await load.ExecuteNonQueryAsync();
        await using var searchPath = new NpgsqlCommand(SearchPathSql, connection);
        await searchPath.ExecuteNonQueryAsync();
    }

    private static bool ExtensionInstalled(NpgsqlConnection connection)
    {
        using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'age')",
            connection);
        return true.Equals(check.ExecuteScalar());
    }

    private static async Task<bool> ExtensionInstalledAsync(NpgsqlConnection connection)
    {
        await using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'age')",
            connection);
        return true.Equals(await check.ExecuteScalarAsync());
    }
}
