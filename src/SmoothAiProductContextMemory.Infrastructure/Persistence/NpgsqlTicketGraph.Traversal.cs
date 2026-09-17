using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

public sealed partial class NpgsqlTicketGraph
{
    private static readonly JsonSerializerOptions TraversalJson = new(JsonSerializerDefaults.Web);

    public async Task<TicketTraversalResult> TraverseAsync(TicketTraversalQuery query, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateTraversalCommandAsync(query, cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        try
        {
            return new TicketTraversalResult(
                JsonSerializer.Deserialize<TicketHierarchyPath[]>(reader.GetString(0), TraversalJson)!,
                JsonSerializer.Deserialize<CheapMemory[]>(reader.GetString(1), TraversalJson)!,
                new TicketTraversalDisclosure(query.MaxDepth, query.PathLimit, query.MemoryLimit,
                    reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }
        catch (JsonException)
        {
            // Stored corruption is not invalid request JSON; omit persisted details from the exception chain.
            throw new InvalidOperationException("Stored ticket traversal data could not be read.");
        }
    }

    // The benchmark EXPLAINs this exact command, including live ownership and hidden-hop gates.
    internal async Task<NpgsqlCommand> CreateTraversalCommandAsync(TicketTraversalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        MutationValidateIdentity(query.Anchor);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.MaxDepth, 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PathLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PathLimit, 200);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MemoryLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.MemoryLimit, 200);
        if (!Enum.IsDefined(query.Direction)) throw new ArgumentOutOfRangeException(nameof(query));

        string outward = "SELECT e.id, e.end_id AS next_id FROM memory_graph.\"TICKET_PARENT\" e WHERE e.start_id = w.id";
        string inward = "SELECT e.id, e.start_id AS next_id FROM memory_graph.\"TICKET_PARENT\" e WHERE e.end_id = w.id";
        string expansion = query.Direction switch
        {
            TraversalDirection.Inbound => inward,
            TraversalDirection.Either => outward + " UNION ALL " + inward,
            _ => outward,
        };
        string anchorCypher = $"MATCH (t:Ticket) WHERE {MutationPredicate("t", query.Anchor)} RETURN id(t)";

        // Expand AGE's indexed adjacency directly after its Cypher anchor lookup. Ownership is live,
        // unique and visible BEFORE expansion: a hidden intermediate kills that entire route. Neither
        // caps nor depth disclosure can therefore count a hidden branch. Property objects are JSON;
        // unlike rendered vertices they need no unsafe annotation replacement on caller strings.
        // Null ineligible owner IDs without filtering the aggregate's cardinality estimate;
        // OFFSET 0 anchors adjacency and keeps the owner join outside the per-vertex expansion.
        NpgsqlCommand command = await MutationCommandAsync($"""
            WITH RECURSIVE memberships AS MATERIALIZED (
                SELECT DISTINCT g.id, g.scope_dimension, ticket->>'provider' AS provider, ticket->>'key' AS key
                FROM public.memory_group g CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(g.tickets) = 'array' THEN g.tickets ELSE '[]'::jsonb END) ticket
                -- Text extraction must not turn a corrupt numeric identity into a string identity.
                WHERE jsonb_typeof(ticket->'provider') = 'string' AND jsonb_typeof(ticket->'key') = 'string'
            ), identities AS MATERIALIZED (
                SELECT id, properties::text::jsonb AS identity FROM memory_graph."Ticket"
            ), owners AS MATERIALIZED (
                SELECT CASE WHEN count(*) = 1 AND bool_and(g.scope_dimension <> ALL(@hidden))
                            THEN t.id END AS id, t.identity,
                       min(g.id) AS group_id, min(g.scope_dimension) AS scope_dimension
                FROM identities t
                JOIN memberships g ON g.provider COLLATE "C" = t.identity->>'provider'
                                  AND g.key COLLATE "C" = t.identity->>'key'
                GROUP BY t.id, t.identity
            ), anchor AS (
                SELECT o.* FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(anchorCypher)})
                    AS a(id ag_catalog.agtype)
                JOIN owners o ON o.id = a.id::text::ag_catalog.graphid
            ), walk(id, depth, vertices, edges, sort_key, group_id) AS (
                SELECT id, 0, ARRAY[id], ARRAY[]::ag_catalog.graphid[],
                       ARRAY[identity->>'provider', identity->>'key'], group_id FROM anchor
                UNION ALL
                SELECT e.next_id, e.depth + 1, e.vertices || e.next_id, e.edges || e.id,
                       e.sort_key || ARRAY[o.identity->>'provider', o.identity->>'key'], o.group_id
                FROM (
                    SELECT e.id, e.next_id, w.depth, w.vertices, w.edges, w.sort_key
                    FROM walk w CROSS JOIN LATERAL ({expansion} OFFSET 0) e
                    WHERE w.depth < @depth + 1 AND NOT e.next_id = ANY(w.vertices)
                    OFFSET 0
                ) e
                JOIN owners o ON o.id = e.next_id
            ), admitted AS MATERIALIZED (
                SELECT * FROM walk WHERE depth BETWEEN 1 AND @depth
            ), selected AS MATERIALIZED (
                SELECT * FROM admitted ORDER BY depth, sort_key COLLATE "C", edges LIMIT @paths
            ), groups AS (
                SELECT group_id FROM anchor
                UNION
                SELECT group_id FROM selected
            ), memories AS MATERIALIZED (
                SELECT m.uuid, g.uuid AS group_uuid, m.name, m.description, v.statement, v.content_summary,
                       v.kind, m.facets, m.tags, v.status, v.confidence, g.scope_dimension, g.scope_identifier,
                        v.valid_from, v.valid_until, v.version, v.is_current, v.sources, v.created_on
                FROM groups selected_group
                JOIN public.memory_group g ON g.id = selected_group.group_id
                JOIN public.memory m ON m.group_id = g.id
                JOIN public.memory_version v ON v.memory_id = m.id AND v.is_current
                WHERE v.status <> 'proposed' AND (@kind IS NULL OR v.kind = @kind)
                  AND (@required IS NULL OR g.scope_dimension = @required)
                  AND g.scope_dimension <> ALL(@hidden)
            ), memory_selection AS (
                SELECT * FROM memories ORDER BY uuid, version LIMIT @memories
            )
            SELECT COALESCE((SELECT jsonb_agg(jsonb_build_object('depth', s.depth, 'hops', (
                       SELECT jsonb_agg(jsonb_build_object(
                           'parent', p.properties::text::jsonb, 'child', c.properties::text::jsonb,
                           'reason', e.properties::text::jsonb->>'reason',
                           'source', e.properties::text::jsonb->>'source',
                           'observedAt', e.properties::text::jsonb->>'observedAt',
                           'recordedAt', e.properties::text::jsonb->>'recordedAt') ORDER BY h.ordinal)
                       FROM unnest(s.edges) WITH ORDINALITY h(id, ordinal)
                       CROSS JOIN LATERAL (
                           SELECT * FROM memory_graph."TICKET_PARENT" edge WHERE edge.id = h.id OFFSET 0
                       ) e
                       JOIN memory_graph."Ticket" p ON p.id = e.start_id
                       JOIN memory_graph."Ticket" c ON c.id = e.end_id
                   ))
                       ORDER BY s.depth, s.sort_key COLLATE "C", s.edges) FROM selected s), '[]'::jsonb)::text,
                   COALESCE((SELECT jsonb_agg(jsonb_build_object(
                       'uuid', uuid, 'groupUuid', group_uuid, 'name', name, 'description', description,
                       'statement', statement, 'contentSummary', content_summary, 'kind', kind,
                       'facets', facets, 'tags', tags, 'status', status, 'confidence', confidence,
                       'scopeDimension', scope_dimension, 'scopeIdentifier', scope_identifier,
                       'validFrom', valid_from, 'validUntil', valid_until, 'version', version,
                        'isCurrent', is_current, 'sources', sources, 'createdOn', created_on)
                        ORDER BY uuid, version) FROM memory_selection), '[]'::jsonb)::text,
                   EXISTS (SELECT 1 FROM walk WHERE depth > @depth),
                   (SELECT count(*) > @paths FROM admitted),
                   (SELECT count(*) > @memories FROM memories);
            """, cancellationToken);
        command.Parameters.AddWithValue("hidden", NpgsqlDbType.Array | NpgsqlDbType.Text, query.HiddenDimensions.ToArray());
        command.Parameters.AddWithValue("depth", query.MaxDepth);
        command.Parameters.AddWithValue("paths", query.PathLimit);
        command.Parameters.AddWithValue("memories", query.MemoryLimit);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = query.Kind ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("required", NpgsqlDbType.Text) { Value = query.RequiredScopeDimension ?? (object)DBNull.Value });
        return command;
    }
}
