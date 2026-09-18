using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// AGE relationship access on the EF connection and ambient transaction (LADR-01).
/// Vertices carry <c>memory_uuid</c> only. Edges are <c>:LINKS</c> with <c>relation</c> + <c>reason</c>.
/// AGE requires <c>cypher()</c>'s map argument to be a prepared-statement parameter of type
/// <c>agtype</c>; Npgsql cannot bind that type, so values are interpolated as Cypher string literals.
/// </summary>
public sealed class NpgsqlMemoryGraph(SmoothAiProductContextMemoryDbContext db) : IMemoryGraph
{
    public async Task<bool> ExistsAsync(
        Guid sourceUuid,
        Guid targetUuid,
        string relation,
        CancellationToken cancellationToken)
    {
        // Property predicates, not an inline map: AGE compiles `{memory_uuid: x}` to `properties @> …`
        // and `WHERE n.memory_uuid = x` to an extracted-property equality. Only the second is served
        // by ix_memory_vertex_uuid (LADR-06). count() over an empty match still yields one row of 0.
        string cypher = $"""
            MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel})
            WHERE s.memory_uuid = {Quote(sourceUuid)}
              AND t.memory_uuid = {Quote(targetUuid)}
              AND e.relation = {Quote(relation)}
            RETURN count(e)
            """;
        string? count = await ExecuteScalarAsync(cypher, cancellationToken);
        return ParseAgtypeInteger(count) > 0;
    }

    public async Task<bool> CreateAsync(
        Guid sourceUuid,
        Guid targetUuid,
        string relation,
        string reason,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            await using IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken);
            bool created = await CreateWithinTransactionAsync(
                sourceUuid, targetUuid, relation, reason, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return created;
        }

        return await CreateWithinTransactionAsync(
            sourceUuid, targetUuid, relation, reason, cancellationToken);
    }

    private async Task<bool> CreateWithinTransactionAsync(
        Guid sourceUuid,
        Guid targetUuid,
        string relation,
        string reason,
        CancellationToken cancellationToken)
    {
        await AcquireLinkWriteLockAsync(cancellationToken);
        if (await ExistsAsync(sourceUuid, targetUuid, relation, cancellationToken))
        {
            return false;
        }

        string cypher = $$"""
            MERGE (s:{{AgeSession.VertexLabel}} {memory_uuid: {{Quote(sourceUuid)}}})
            MERGE (t:{{AgeSession.VertexLabel}} {memory_uuid: {{Quote(targetUuid)}}})
            CREATE (s)-[:{{AgeSession.EdgeLabel}} {relation: {{Quote(relation)}}, reason: {{Quote(reason)}}}]->(t)
            RETURN 1
            """;
        await ExecuteScalarAsync(cypher, cancellationToken);
        return true;
    }

    private async Task AcquireLinkWriteLockAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateRelationalCommandAsync(
            "SELECT pg_advisory_xact_lock(734921, 2);",
            cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlCommand> CreateRelationalCommandAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return new NpgsqlCommand(sql, connection, transaction);
    }

    public Task<IReadOnlyList<MemoryRelationship>> ListAllAsync(CancellationToken cancellationToken) =>
        ListAsync(
            $"""
            MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel})
            RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
            """,
            cancellationToken);

    public Task<IReadOnlyList<MemoryRelationship>> ListTouchingAsync(Guid uuid, CancellationToken cancellationToken) =>
        ListAsync(ListTouchingCypher(uuid), cancellationToken);

    /// <summary>
    /// The one-hop lookup — outbound and inbound edges of one memory — exposed so the NFR-02 benchmark
    /// plans the statement the store runs rather than a hand-written copy of it.
    /// </summary>
    /// <remarks>
    /// Two anchored matches unioned, not one match with <c>OR</c>. A disjunction across two different
    /// vertex instances cannot be served by an index on either, so the OR form hash-joined the whole
    /// edge table against both vertex scans: measured at 6.075 ms p95 over <c>Seq Scan on "LINKS"</c>,
    /// against a 0.429 ms relational baseline. Each union branch anchors one endpoint, so each uses
    /// <c>ix_memory_vertex_uuid</c> (LADR-06). <c>UNION</c> rather than <c>UNION ALL</c> because a
    /// self-link satisfies both branches and must still be reported once.
    /// </remarks>
    internal static string ListTouchingCypher(Guid uuid) =>
        $"""
        MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel})
        WHERE s.memory_uuid = {Quote(uuid)}
        RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
        UNION
        MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel})
        WHERE t.memory_uuid = {Quote(uuid)}
        RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
        """;

    private async Task<IReadOnlyList<MemoryRelationship>> ListAsync(string cypher, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(
            cypher,
            "(s agtype, t agtype, r agtype, reason agtype)",
            cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<MemoryRelationship>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MemoryRelationship(
                Guid.Parse(ReadAgtypeString(reader, 0)),
                Guid.Parse(ReadAgtypeString(reader, 1)),
                ReadAgtypeString(reader, 2),
                ReadAgtypeString(reader, 3)));
        }

        return rows;
    }

    private async Task<string?> ExecuteScalarAsync(string cypher, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(cypher, "(v agtype)", cancellationToken);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    internal async Task<NpgsqlCommand> CreateCommandAsync(
        string cypher,
        string resultColumns,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        return new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = resultColumns.Contains("s agtype", StringComparison.Ordinal)
                ? $"""
                    SELECT s::text, t::text, r::text, reason::text
                    FROM ag_catalog.cypher('{AgeSession.GraphName}', {DollarWrap(cypher)}) AS {resultColumns};
                    """
                : $"""
                    SELECT v::text FROM ag_catalog.cypher('{AgeSession.GraphName}', {DollarWrap(cypher)}) AS {resultColumns};
                    """,
        };
    }

    private static string Quote(Guid uuid) => Quote(uuid.ToString("D"));

    private static string Quote(string value) => CypherLiteral.Quote(value);

    private static string DollarWrap(string cypher) => CypherLiteral.DollarWrap(cypher);

    private static string ReadAgtypeString(NpgsqlDataReader reader, int ordinal)
    {
        string raw = reader.GetString(ordinal);
        if (raw.Length >= 2 && raw[0] == '"')
        {
            int closingQuote = raw.LastIndexOf('"');
            if (closingQuote > 0)
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw[..(closingQuote + 1)]) ?? string.Empty;
            }
        }

        return raw;
    }

    private static long ParseAgtypeInteger(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int typeSuffix = value.IndexOf("::", StringComparison.Ordinal);
        ReadOnlySpan<char> number = typeSuffix >= 0 ? value.AsSpan(0, typeSuffix) : value.AsSpan();
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0;
    }
}
