using System.Globalization;
using System.Text;
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
        string cypher = $$"""
            OPTIONAL MATCH (s:Memory {memory_uuid: {{Quote(sourceUuid)}}})-[e:LINKS {relation: {{Quote(relation)}}}]->(t:Memory {memory_uuid: {{Quote(targetUuid)}}})
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
        if (await ExistsAsync(sourceUuid, targetUuid, relation, cancellationToken))
        {
            return false;
        }

        string cypher = $$"""
            MERGE (s:Memory {memory_uuid: {{Quote(sourceUuid)}}})
            MERGE (t:Memory {memory_uuid: {{Quote(targetUuid)}}})
            CREATE (s)-[:LINKS {relation: {{Quote(relation)}}, reason: {{Quote(reason)}}}]->(t)
            RETURN 1
            """;
        await ExecuteScalarAsync(cypher, cancellationToken);
        return true;
    }

    public Task<IReadOnlyList<MemoryRelationship>> ListAllAsync(CancellationToken cancellationToken) =>
        ListAsync(
            """
            MATCH (s:Memory)-[e:LINKS]->(t:Memory)
            RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
            """,
            cancellationToken);

    public Task<IReadOnlyList<MemoryRelationship>> ListTouchingAsync(Guid uuid, CancellationToken cancellationToken) =>
        ListAsync(
            $"""
            MATCH (s:Memory)-[e:LINKS]->(t:Memory)
            WHERE s.memory_uuid = {Quote(uuid)} OR t.memory_uuid = {Quote(uuid)}
            RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
            """,
            cancellationToken);

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

    private async Task<NpgsqlCommand> CreateCommandAsync(
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
                    FROM ag_catalog.cypher('{AgeSession.GraphName}', $$
                    {cypher}
                    $$) AS {resultColumns};
                    """
                : $"""
                    SELECT v::text FROM ag_catalog.cypher('{AgeSession.GraphName}', $$
                    {cypher}
                    $$) AS {resultColumns};
                    """,
        };
    }

    private static string Quote(Guid uuid) => Quote(uuid.ToString("D"));

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\'':
                    builder.Append("\\'");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private static string ReadAgtypeString(NpgsqlDataReader reader, int ordinal)
    {
        string raw = reader.GetString(ordinal);
        int typeSuffix = raw.IndexOf("::", StringComparison.Ordinal);
        if (typeSuffix >= 0)
        {
            raw = raw[..typeSuffix];
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
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
