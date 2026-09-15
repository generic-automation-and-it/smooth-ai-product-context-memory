using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Exceptions;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

public sealed partial class NpgsqlTicketGraph(SmoothAiProductContextMemoryDbContext db) : ITicketGraph
{
    private const string MutationSavepoint = "ticket_graph_parent_change";

    public async Task LockAsync(CancellationToken cancellationToken)
    {
        MutationRequireTransaction();
        await using NpgsqlCommand command = await MutationCommandAsync(
            "SELECT pg_advisory_xact_lock(734921, 1);", cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private IDbContextTransaction MutationRequireTransaction()
    {
        IDbContextTransaction transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Ticket graph locking requires an explicit EF transaction.");
        if (transaction.GetDbTransaction().IsolationLevel != System.Data.IsolationLevel.ReadCommitted)
        {
            throw new InvalidOperationException("Ticket graph mutation requires ReadCommitted isolation.");
        }

        return transaction;
    }

    public async Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        MutationValidateIdentity(change.Child);
        if (change.Parent is not null) MutationValidateIdentity(change.Parent);
        if (change.ExpectedParent is not null) MutationValidateIdentity(change.ExpectedParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Source);
        if (change.Reason.Length > 4000 || change.Source.Length > 4000
            || change.Reason.Contains('\0') || change.Source.Contains('\0'))
        {
            throw new ArgumentException("Declaration metadata is invalid.");
        }

        await using IDbContextTransaction? transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        IDbContextTransaction activeTransaction = MutationRequireTransaction();
        if (transaction is null) await activeTransaction.CreateSavepointAsync(MutationSavepoint, cancellationToken);
        try
        {
            await LockAsync(cancellationToken);
            await MutationRequireOwnerAsync(change.Child, cancellationToken);
            if (change.Parent is not null) await MutationRequireOwnerAsync(change.Parent, cancellationToken);
            if (change.ExpectedParent is not null) await MutationRequireOwnerAsync(change.ExpectedParent, cancellationToken);

            TicketHierarchyHop? current = await MutationCurrentAsync(change.Child, cancellationToken);
            if (current?.Parent != change.ExpectedParent)
            {
                throw MutationConflict();
            }

            bool identical = current is null
                ? change.Parent is null
                : current.Parent == change.Parent && current.Reason == change.Reason && current.Source == change.Source
                    && current.ObservedAt == change.ObservedAt;
            if (!identical)
            {
                if (change.Parent is not null
                    && (change.Parent == change.Child || await MutationWouldCycleAsync(change.Child, change.Parent, cancellationToken)))
                {
                    throw MutationConflict();
                }

                string cypher;
                if (change.Parent is null)
                {
                    cypher = $"""
                        MATCH (c:Ticket)<-[e:TICKET_PARENT]-(:Ticket)
                        WHERE {MutationPredicate("c", change.Child)}
                        DELETE e RETURN 1
                        """;
                }
                else
                {
                    string observed = change.ObservedAt is null ? string.Empty
                        : $", observedAt: {CypherLiteral.Quote(change.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture))}";
                    string oldParent = current is null ? string.Empty : "<-[old:TICKET_PARENT]-(:Ticket)";
                    string delete = current is null ? string.Empty : "DELETE old WITH p, c";
                    cypher = $$"""
                        MATCH (p:Ticket), (c:Ticket){{oldParent}}
                        WHERE {{MutationPredicate("p", change.Parent)}} AND {{MutationPredicate("c", change.Child)}}
                        {{delete}}
                        CREATE (p)-[:TICKET_PARENT {reason: {{CypherLiteral.Quote(change.Reason)}},
                            source: {{CypherLiteral.Quote(change.Source)}},
                            recordedAt: {{CypherLiteral.Quote(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))}}{{observed}}}]->(c)
                        RETURN 1
                        """;
                }

                await MutationExecuteAsync(cypher, cancellationToken);
            }

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            else await activeTransaction.ReleaseSavepointAsync(MutationSavepoint, cancellationToken);
            return !identical;
        }
        catch
        {
            if (transaction is null)
            {
                await activeTransaction.RollbackToSavepointAsync(MutationSavepoint, CancellationToken.None);
                await activeTransaction.ReleaseSavepointAsync(MutationSavepoint, CancellationToken.None);
            }

            throw;
        }
    }

    private static void MutationValidateIdentity(TicketIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Key);
        if (identity.Provider.Length > 512 || identity.Key.Length > 512
            || identity.Provider.Contains('\0') || identity.Key.Contains('\0'))
        {
            throw new ArgumentException("Ticket identity is invalid.");
        }
    }

    private async Task MutationRequireOwnerAsync(TicketIdentity identity, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await MutationCommandAsync(
            """
            SELECT count(*) FROM public.memory_group g
            WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(
                CASE WHEN jsonb_typeof(g.tickets) = 'array' THEN g.tickets ELSE '[]'::jsonb END) t
                WHERE jsonb_typeof(t->'provider') = 'string' AND jsonb_typeof(t->'key') = 'string'
                    AND t->>'provider' COLLATE "C" = @provider AND t->>'key' COLLATE "C" = @key);
            """, cancellationToken);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("key", identity.Key);
        long count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count == 0) throw new NotFoundException("Ticket ownership could not be resolved.");
        if (count != 1) throw MutationConflict();

        string cypher = $"MATCH (t:Ticket) WHERE {MutationPredicate("t", identity)} RETURN id(t)";
        await using NpgsqlCommand vertices = await MutationCommandAsync(
            $"SELECT count(*) FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)}) AS (id ag_catalog.agtype);",
            cancellationToken);
        if ((long)(await vertices.ExecuteScalarAsync(cancellationToken))! != 1) throw MutationConflict();
    }

    private async Task<TicketHierarchyHop?> MutationCurrentAsync(TicketIdentity child, CancellationToken cancellationToken)
    {
        string cypher = $"""
            MATCH (p:Ticket)-[e:TICKET_PARENT]->(c:Ticket)
            WHERE {MutationPredicate("c", child)}
            RETURN p.provider, p.key, e.reason, e.source, e.observedAt, e.recordedAt
            """;
        await using NpgsqlCommand command = await MutationCommandAsync(
            $"""
            SELECT provider::text, key::text, reason::text, source::text, observed::text, recorded::text
            FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)})
                AS (provider ag_catalog.agtype, key ag_catalog.agtype, reason ag_catalog.agtype,
                    source ag_catalog.agtype, observed ag_catalog.agtype, recorded ag_catalog.agtype);
            """, cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new TicketHierarchyHop(new TicketIdentity(MutationString(reader, 0), MutationString(reader, 1)), child,
            MutationString(reader, 2), MutationString(reader, 3),
            reader.IsDBNull(4) || reader.GetString(4) == "null" ? null
                : DateTimeOffset.Parse(MutationString(reader, 4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(MutationString(reader, 5), CultureInfo.InvariantCulture));
        if (await reader.ReadAsync(cancellationToken)) throw MutationConflict();
        return result;
    }

    private async Task<bool> MutationWouldCycleAsync(TicketIdentity child, TicketIdentity parent, CancellationToken cancellationToken)
    {
        // UNION deduplicates visited graph ids, terminating even if pre-existing data is cyclic.
        // Integrity is unbounded and independent of traversal visibility/depth limits.
        string cypher = $"MATCH (c:Ticket), (p:Ticket) WHERE {MutationPredicate("c", child)} AND {MutationPredicate("p", parent)} RETURN id(c), id(p)";
        await using NpgsqlCommand command = await MutationCommandAsync(
            $"""
            WITH RECURSIVE endpoints AS (
                SELECT child::text::ag_catalog.graphid AS child, parent::text::ag_catalog.graphid AS parent
                FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)})
                    AS (child ag_catalog.agtype, parent ag_catalog.agtype)
            ), reachable(id) AS (
                SELECT child FROM endpoints
                UNION
                SELECT e.end_id FROM memory_graph."TICKET_PARENT" e JOIN reachable r ON e.start_id = r.id
            )
            SELECT EXISTS (SELECT 1 FROM reachable r JOIN endpoints p ON r.id = p.parent);
            """, cancellationToken);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task MutationExecuteAsync(string cypher, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await MutationCommandAsync(
            $"SELECT v::text FROM ag_catalog.cypher('memory_graph', {CypherLiteral.DollarWrap(cypher)}) AS (v ag_catalog.agtype);",
            cancellationToken);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || await reader.ReadAsync(cancellationToken)) throw MutationConflict();
    }

    private async Task<NpgsqlCommand> MutationCommandAsync(string sql, CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        return new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
    }

    private static string MutationPredicate(string variable, TicketIdentity identity) =>
        $"{variable}.provider = {CypherLiteral.Quote(identity.Provider)} AND {variable}.key = {CypherLiteral.Quote(identity.Key)}";

    private static string MutationString(NpgsqlDataReader reader, int ordinal) =>
        reader.GetString(ordinal);

    private static ConflictException MutationConflict() => new("Ticket hierarchy change conflicts with current state.");
}
