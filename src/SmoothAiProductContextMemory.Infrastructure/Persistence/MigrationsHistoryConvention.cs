using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Pins the migrations-history table to an explicit schema.
/// </summary>
/// <remarks>
/// <see cref="AgeSession"/> sets <c>search_path = ag_catalog, "$user", public</c> on every physical
/// connection once the AGE extension exists, which makes <c>current_schema()</c> return
/// <c>ag_catalog</c>. EF resolves an unqualified history table against the current schema, so it
/// looks in <c>ag_catalog</c>, finds nothing, concludes the database has never been migrated and
/// re-applies the first migration — which fails on the already-present
/// <c>append_only_guard</c> function. The first start of a fresh database succeeds because AGE is not
/// installed yet when history is first read; every start after that fails.
/// <para>
/// Every test tier recreates its database, so each run is always a "first start" and the suite never
/// saw this. The schema named here is where the table already lives, so pinning it moves no data.
/// </para>
/// </remarks>
public static class MigrationsHistoryConvention
{
    public const string Schema = "public";

    public static NpgsqlDbContextOptionsBuilder UseSmoothAiProductContextMemoryHistory(
        this NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);

        return npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schema);
    }
}
