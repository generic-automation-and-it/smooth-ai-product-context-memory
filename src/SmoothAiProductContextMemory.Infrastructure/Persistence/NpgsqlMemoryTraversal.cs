using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Bounded traversal composed with the relational read in one statement (LADR-01).
/// </summary>
/// <remarks>
/// The Cypher call supplies identities and hops; <c>memory</c>, <c>memory_version</c> and
/// <c>memory_group</c> supply what those memories say, joined in the same SQL rather than by a second
/// round trip. Cypher values are interpolated because AGE requires <c>cypher()</c>'s map argument to
/// be an <c>agtype</c> prepared-statement parameter that Npgsql cannot bind; every relational
/// predicate sits outside the Cypher literal and is a real parameter.
/// </remarks>
public sealed class NpgsqlMemoryTraversal(SmoothAiProductContextMemoryDbContext db) : IMemoryTraversal
{
    private static readonly JsonSerializerOptions TraversalJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MemoryPath>> FindPathsAsync(
        MemoryPathQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.MaxDepth, MemoryTraversalDefaults.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MemorySearchDefaults.MaxLimit);

        await using NpgsqlCommand command = await CreateCommandAsync(query, cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        var paths = new List<MemoryPath>();
        while (await reader.ReadAsync(cancellationToken))
        {
            MemoryPathHop[] hops = ReadHops(reader.GetString(0), reader.GetString(1));
            paths.Add(new MemoryPath(hops.Length, hops, ReadEndpoint(reader)));
        }

        return paths;
    }

    public async Task<MemoryWidenResult> WidenAsync(
        MemoryWidenQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Store-layer bound refusal, mirroring FindPathsAsync. A direct caller bypassing the request
        // validator must not get an unbounded widening, because the bound is the export's cost control
        // and completeness claim (HLD-005 NFR-03).
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.MaxDepth, MemoryTraversalDefaults.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MemorySearchDefaults.MaxLimit);
        if (query.SourceUuids.Count == 0)
        {
            throw new ArgumentException("SourceUuids must not be empty.", nameof(query));
        }

        await using NpgsqlCommand command = await CreateWidenCommandAsync(query, cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        try
        {
            var memories = JsonSerializer.Deserialize<CheapMemory[]>(reader.GetString(0), TraversalJson) ?? [];
            return new MemoryWidenResult(
                memories,
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Stored widening data could not be read.");
        }
    }

    internal async Task<NpgsqlCommand> CreateWidenCommandAsync(
        MemoryWidenQuery query,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = BuildWidenSql(query),
        };

        command.Parameters.Add(new NpgsqlParameter("depth", NpgsqlDbType.Integer) { Value = query.MaxDepth });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = query.Kind ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = query.Status ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("requiredScope", NpgsqlDbType.Text)
        {
            Value = query.RequiredScopeDimension ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("hiddenScopes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = query.HiddenDimensions.ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = query.Limit });

        return command;
    }

    private static string BuildWidenSql(MemoryWidenQuery query)
    {
        string sources = string.Join(", ", query.SourceUuids.Select(u => CypherLiteral.Quote(u.ToString("D"))));
        string edge = $"[:{AgeSession.EdgeLabel}*1..{query.MaxDepth.ToString(CultureInfo.InvariantCulture)}]";
        string pattern = query.Direction switch
        {
            TraversalDirection.Inbound => $"(s:{AgeSession.VertexLabel})<-{edge}-(t:{AgeSession.VertexLabel})",
            TraversalDirection.Either => $"(s:{AgeSession.VertexLabel})-{edge}-(t:{AgeSession.VertexLabel})",
            _ => $"(s:{AgeSession.VertexLabel})-{edge}->(t:{AgeSession.VertexLabel})",
        };

        string cypher = $"""
            MATCH p = {pattern}
            WHERE s.memory_uuid IN [{sources}]
            RETURN nodes(p), length(p)
            """;

        string cypherDollar = CypherLiteral.DollarWrap(cypher);
        // Every vertex a path crosses is gated, not only the endpoints. A path through a hidden memory
        // is dropped whole, never shortened (HLD-005 NFR-01). The same text surgery the proven
        // single-source statement applies to vertex rendering is reused here.
        const string nodeList = "replace(tr.nodes::text, '::vertex', '')::jsonb";
        const string crossesHidden = $"""
            EXISTS (
                SELECT 1 FROM jsonb_array_elements({nodeList}) AS hop_node
                JOIN memory hop_memory ON hop_memory.uuid = (hop_node -> 'properties' ->> 'memory_uuid')::uuid
                JOIN memory_group hop_group ON hop_group.id = hop_memory.group_id
                WHERE hop_group.scope_dimension = ANY(@hiddenScopes)
            )
            """;

        return $"""
            WITH walked AS MATERIALIZED (
                SELECT tr.nodes::text AS nodes, tr.depth AS depth,
                       {crossesHidden} AS hidden
                FROM ag_catalog.cypher('{AgeSession.GraphName}', {cypherDollar})
                    AS tr(nodes ag_catalog.agtype, depth ag_catalog.agtype)
            ), surviving AS MATERIALIZED (
                SELECT nodes FROM walked WHERE NOT hidden
            ), reached AS MATERIALIZED (
                SELECT DISTINCT m.uuid, g.uuid AS group_uuid, m.name, m.description, v.statement,
                       v.content_summary, v.kind, m.facets, m.tags, v.status, v.confidence,
                       g.scope_dimension, g.scope_identifier, v.valid_from, v.valid_until, v.version,
                       v.is_current, v.sources, v.created_on
                FROM surviving sv
                CROSS JOIN LATERAL jsonb_array_elements(replace(sv.nodes, '::vertex', '')::jsonb) AS node
                JOIN memory m ON m.uuid = (node -> 'properties' ->> 'memory_uuid')::uuid
                JOIN memory_group g ON g.id = m.group_id
                JOIN memory_version v ON v.memory_id = m.id AND v.is_current
                WHERE (@requiredScope IS NULL OR g.scope_dimension = @requiredScope)
                  AND (@kind IS NULL OR v.kind = @kind)
                  AND (@status IS NULL OR v.status = @status)
            ), ranked AS MATERIALIZED (
                SELECT * FROM reached
                ORDER BY valid_from, created_on, uuid
                LIMIT @limit
            )
            SELECT COALESCE((SELECT jsonb_agg(jsonb_build_object(
                       'uuid', uuid, 'groupUuid', group_uuid, 'name', name, 'description', description,
                       'statement', statement, 'contentSummary', content_summary, 'kind', kind,
                       'facets', facets, 'tags', tags, 'status', status, 'confidence', confidence,
                       'scopeDimension', scope_dimension, 'scopeIdentifier', scope_identifier,
                       'validFrom', valid_from, 'validUntil', valid_until, 'version', version,
                       'isCurrent', is_current, 'sources', sources, 'createdOn', created_on)
                       ORDER BY valid_from, created_on, uuid) FROM ranked), '[]'::jsonb)::text,
                   EXISTS (SELECT 1 FROM walked WHERE (depth::text)::int >= @depth),
                   (SELECT count(*) > @limit FROM reached),
                   EXISTS (SELECT 1 FROM walked WHERE hidden);
            """;
    }

    /// <summary>
    /// Internal so the NFR-02 benchmark can <c>EXPLAIN</c> the statement the store actually runs.
    /// A benchmark that plans a hand-written copy measures the copy.
    /// </summary>
    internal async Task<NpgsqlCommand> CreateCommandAsync(MemoryPathQuery query, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = transaction,
            CommandText = BuildSql(BuildCypher(query)),
        };

        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = query.Kind ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = query.Status ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("requiredScope", NpgsqlDbType.Text)
        {
            Value = query.RequiredScopeDimension ?? (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("excludedScopes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = query.ExcludedScopeDimensions.ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = query.Limit });

        return command;
    }

    private static string BuildCypher(MemoryPathQuery query)
    {
        string relationFilter = query.Relation is null
            ? string.Empty
            : $" {{relation: {Quote(query.Relation)}}}";
        string edge = $"[:{AgeSession.EdgeLabel}*1..{query.MaxDepth.ToString(CultureInfo.InvariantCulture)}{relationFilter}]";
        string pattern = query.Direction switch
        {
            TraversalDirection.Inbound => $"(s:{AgeSession.VertexLabel})<-{edge}-(t:{AgeSession.VertexLabel})",
            TraversalDirection.Either => $"(s:{AgeSession.VertexLabel})-{edge}-(t:{AgeSession.VertexLabel})",
            _ => $"(s:{AgeSession.VertexLabel})-{edge}->(t:{AgeSession.VertexLabel})",
        };

        // Property predicates rather than inline maps so ix_memory_vertex_uuid serves both anchors
        // (LADR-06). The hop bound is inside the pattern; there is no unbounded form (LADR-07).
        string endpointFilter = query.TargetUuid is { } target
            ? $"\n              AND t.memory_uuid = {Quote(target)}"
            : string.Empty;

        return $"""
            MATCH p = {pattern}
            WHERE s.memory_uuid = {Quote(query.SourceUuid)}{endpointFilter}
            RETURN nodes(p), relationships(p), t.memory_uuid
            """;
    }

    private static string BuildSql(string cypher) =>
        $"""
        SELECT tr.nodes::text,
               tr.hops::text,
               m.uuid,
               g.uuid,
               m.name,
               m.description,
               v.statement,
               v.content_summary,
               v.kind,
               m.facets,
               m.tags,
               v.status,
               v.confidence,
               g.scope_dimension,
               g.scope_identifier,
               v.valid_from,
               v.valid_until,
               v.version,
               v.is_current,
               v.sources::text,
               v.created_on
        FROM ag_catalog.cypher('{AgeSession.GraphName}', {DollarWrap(cypher)})
                 AS tr(nodes ag_catalog.agtype, hops ag_catalog.agtype, endpoint ag_catalog.agtype)
        JOIN memory m ON m.uuid = trim(both '"' from tr.endpoint::text)::uuid
        JOIN memory_version v ON v.memory_id = m.id AND v.is_current
        JOIN memory_group g ON g.id = m.group_id
        WHERE (@kind IS NULL OR v.kind = @kind)
          AND (@status IS NULL OR v.status = @status)
          AND (@requiredScope IS NULL OR g.scope_dimension = @requiredScope)
          AND g.scope_dimension <> ALL(@excludedScopes)
          -- Intermediate hops are gated too, not only the endpoint. A path from a product memory
          -- through a programme one discloses that memory's identity and its edges' reasons, which is
          -- descriptive content on a read path. Only the excluded list is a visibility rule;
          -- @requiredScope is a selector for which endpoints to return, so narrowing to a dimension
          -- must not forbid routing through freely-readable ones.
          --
          -- Text surgery on the node list is safe where it would not be on the edge list: a vertex
          -- carries memory_uuid and nothing else (LADR-02), so no caller-supplied string can appear in
          -- its rendering. Edges carry `reason` and are parsed by AgtypeArrayReader instead.
          --
          -- Gating hops roughly doubles this shape — 8.4 ms without it, ~14 ms with — and two SQL
          -- formulations measured the same, so the simpler one is kept. The short-circuit is a real win
          -- rather than cosmetic: a caller reading as programme hides nothing, and pays nothing.
          AND (cardinality(@excludedScopes) = 0 OR NOT EXISTS (
              SELECT 1
              FROM jsonb_array_elements(replace(tr.nodes::text, '::vertex', '')::jsonb) AS hop_node
              JOIN memory hop_memory
                  ON hop_memory.uuid = (hop_node -> 'properties' ->> 'memory_uuid')::uuid
              JOIN memory_group hop_group ON hop_group.id = hop_memory.group_id
              WHERE hop_group.scope_dimension = ANY(@excludedScopes)
          ))
        LIMIT @limit;
        """;

    private static MemoryPathHop[] ReadHops(string nodesAgtype, string hopsAgtype)
    {
        using JsonDocument nodes = JsonDocument.Parse(AgtypeArrayReader.ToJson(nodesAgtype));
        using JsonDocument edges = JsonDocument.Parse(AgtypeArrayReader.ToJson(hopsAgtype));

        var uuidByVertexId = new Dictionary<long, Guid>();
        foreach (JsonElement node in nodes.RootElement.EnumerateArray())
        {
            uuidByVertexId[node.GetProperty("id").GetInt64()] =
                Guid.Parse(node.GetProperty("properties").GetProperty("memory_uuid").GetString()!);
        }

        // Orientation comes from the edge, never from the order the path walked it: an Either-direction
        // traversal crosses edges backwards, and direction is part of a relationship's identity.
        return [.. edges.RootElement.EnumerateArray().Select(edge => new MemoryPathHop(
            uuidByVertexId[edge.GetProperty("start_id").GetInt64()],
            uuidByVertexId[edge.GetProperty("end_id").GetInt64()],
            edge.GetProperty("properties").GetProperty("relation").GetString()!,
            edge.GetProperty("properties").GetProperty("reason").GetString()!))];
    }

    private static CheapMemory ReadEndpoint(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<string[]>(9),
            reader.GetFieldValue<string[]>(10),
            reader.GetString(11),
            reader.GetInt16(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetInt32(17),
            reader.GetBoolean(18),
            JsonSerializer.Deserialize<List<SourceDocument>>(
                reader.GetString(19),
                TraversalJson) ?? [],
            reader.GetFieldValue<DateTimeOffset>(20));

    private static string Quote(Guid uuid) => Quote(uuid.ToString("D"));

    private static string Quote(string value) => CypherLiteral.Quote(value);

    private static string DollarWrap(string cypher) => CypherLiteral.DollarWrap(cypher);
}
