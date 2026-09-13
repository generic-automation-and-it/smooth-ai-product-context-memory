using Npgsql;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

public sealed class AgeFoundationTests : PersistenceTestBase
{
    private const int PoolSize = 8;
    private const string ProbeGraph = "age_visibility_probe";
    private static readonly string GraphStatement =
        $"SELECT v::text FROM cypher('{AgeSession.GraphName}', $$ RETURN 1 $$) AS (v agtype);";

    public AgeFoundationTests(AspireFixture aspire) : base(aspire) { }

    [Fact]
    public async Task GraphStatement_SucceedsOnEveryPooledConnection_AfterExhaustAndRecycle()
    {
        await using NpgsqlDataSource dataSource = CreatePooledDataSource(PoolSize);

        await AssertGraphStatementOnEveryConnection(dataSource, PoolSize);
        await AssertGraphStatementOnEveryConnection(dataSource, PoolSize);
    }

    [Fact]
    public async Task GraphCreatedInOneSession_IsVisibleFromANewSession()
    {
        await using (var creator = await DataSource.OpenConnectionAsync(Ct))
        {
            await using var create = new NpgsqlCommand($"SELECT create_graph('{ProbeGraph}');", creator);
            await create.ExecuteNonQueryAsync(Ct);
        }

        await using (var reader = await DataSource.OpenConnectionAsync(Ct))
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM ag_catalog.ag_graph WHERE name = $1;",
                reader)
            {
                Parameters = { new NpgsqlParameter { Value = ProbeGraph } },
            };
            Convert.ToInt64(await count.ExecuteScalarAsync(Ct)).ShouldBe(1);
        }

        await using (var cleaner = await DataSource.OpenConnectionAsync(Ct))
        {
            await using var drop = new NpgsqlCommand($"SELECT drop_graph('{ProbeGraph}', true);", cleaner);
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task UnpreparedConnection_CannotParseGraphStatement()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false };
        await using var raw = new NpgsqlConnection(builder.ConnectionString);
        await raw.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(GraphStatement, raw);

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteScalarAsync(Ct));
        ex.SqlState.ShouldNotBeNull();
    }

    [Fact]
    public async Task Migration_CreatesExtensionGraphAndLabels_VisibleToNewSession()
    {
        await using var conn = await DataSource.OpenConnectionAsync(Ct);

        await using (var ext = new NpgsqlCommand(
            "SELECT extversion FROM pg_extension WHERE extname = 'age';",
            conn))
        {
            (await ext.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
        }

        await using (var graphs = new NpgsqlCommand(
            $"SELECT count(*) FROM ag_catalog.ag_graph WHERE name = '{AgeSession.GraphName}';",
            conn))
        {
            Convert.ToInt64(await graphs.ExecuteScalarAsync(Ct)).ShouldBe(1);
        }

        await using (var labels = new NpgsqlCommand(
            $"""
            SELECT name::text
            FROM ag_catalog.ag_label
            WHERE graph = (SELECT graphid FROM ag_catalog.ag_graph WHERE name = '{AgeSession.GraphName}')
              AND name = ANY(ARRAY['{AgeSession.VertexLabel}','depends_on','relates_to','contradicts','supersedes','implements']);
            """,
            conn))
        await using (var reader = await labels.ExecuteReaderAsync(Ct))
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(Ct))
            {
                found.Add(reader.GetString(0));
            }

            found.Count.ShouldBe(6);
            found.ShouldContain(AgeSession.VertexLabel);
            found.ShouldContain("depends_on");
            found.ShouldContain("relates_to");
            found.ShouldContain("contradicts");
            found.ShouldContain("supersedes");
            found.ShouldContain("implements");
        }
    }

    private NpgsqlDataSource CreatePooledDataSource(int poolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            MaxPoolSize = poolSize,
            MinPoolSize = 0,
        };
        return NpgsqlDataSourceFactory.Create(builder.ConnectionString);
    }

    private async Task AssertGraphStatementOnEveryConnection(NpgsqlDataSource dataSource, int count)
    {
        var connections = new List<NpgsqlConnection>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                NpgsqlConnection conn = await dataSource.OpenConnectionAsync(Ct);
                connections.Add(conn);
                await using var cmd = new NpgsqlCommand(GraphStatement, conn);
                (await cmd.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
            }
        }
        finally
        {
            foreach (NpgsqlConnection conn in connections)
            {
                await conn.DisposeAsync();
            }
        }
    }
}
