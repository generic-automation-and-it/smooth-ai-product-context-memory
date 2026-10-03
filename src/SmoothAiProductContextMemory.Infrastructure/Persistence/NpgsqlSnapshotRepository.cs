using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence.Configurations;
using SmoothAiProductContextMemory.Infrastructure.Storage;
using SmoothAiProductContextMemory.Infrastructure.Storage.Snapshot;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Captures and restores both stores over one data source built from the supplied connection string,
/// inside a single transaction so relational rows, AGE vertices/edges and the blob-reference set
/// describe the same moment. The graph is outside the EF model, so it is read/written via raw
/// <c>ag_catalog.cypher</c> on the same connection as the relational SQL (LADR-03 / LADR-05 / LADR-07).
/// </summary>
public sealed class NpgsqlSnapshotRepository(
    IBlobStorage blobStorage,
    IBlobCatalog blobCatalog,
    SnapshotMetadataOptions? options = null)
    : ISnapshotRepository
{
    private readonly int? _configuredRestoreTimeoutSeconds = options?.RestoreStatementTimeoutSeconds;

    private const IsolationLevel CaptureIsolation = IsolationLevel.RepeatableRead;
    private const IsolationLevel RestoreIsolation = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Per-statement budget for the restore transaction, in seconds. The data source's 120 s cap
    /// (HLD-003 LADR-07) is the right default for request traffic, where it is the long stop behind
    /// the traversal bounds — but restore is a bulk operation whose statements are sized by the
    /// corpus, not by a request. Two statements in particular grow without bound: EF batched the
    /// whole capture's <c>memory_version</c> rows into one INSERT, and <c>DELETE FROM memory</c> runs
    /// a per-row cascade trigger for every row in the target. Past a few tens of thousands of rows
    /// either exceeds 120 s, the restore rolls back whole, and there was no knob to turn.
    /// </summary>
    /// <remarks>
    /// Applied twice, and both halves are load-bearing. <c>SET LOCAL</c> scopes the server-side
    /// <c>statement_timeout</c> to the restore transaction and reverts it on commit or rollback; the
    /// data source for the same operation is built with this value as its client-side
    /// <c>CommandTimeout</c>, because the client abandons a statement at its own cap regardless of what
    /// the server is willing to wait for. Raising only the server side left the effective budget at
    /// <c>min(120, configured)</c>, so a large corpus rolled back at 120 s with no knob that changed
    /// it. The request-traffic default is untouched everywhere else, and the batching below keeps any
    /// single statement small enough that this ceiling is a backstop rather than the thing standing
    /// between a large corpus and a successful restore. Configurable through
    /// <c>Snapshot__RestoreStatementTimeoutSeconds</c>.
    /// </remarks>
    private const int DefaultRestoreStatementTimeoutSeconds = 900;

    /// <summary>
    /// Ceiling on <see cref="DefaultRestoreStatementTimeoutSeconds"/>'s override. A day is far longer
    /// than any legitimate restore statement and still short enough that a wedged one is caught.
    /// </summary>
    private const int MaxRestoreStatementTimeoutSeconds = 86_400;

    /// <summary>
    /// Rows per EF batch during restore. Bounds the largest statement the insert path can emit, which
    /// is what actually made restore scale badly — the timeout raise is the backstop behind it.
    /// </summary>
    private const int RestoreBatchSize = 500;

    public async Task<SnapshotCaptureResult> CaptureAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // The restore budget, not the 120 s request default: capture is a bulk read over a corpus of
        // the same shape restore writes, and a target too large to snapshot within 120 s is a target
        // whose size is exactly what the restore budget exists for.
        await using NpgsqlDataSource dataSource =
            NpgsqlDataSourceFactory.Create(connectionString, RestoreStatementTimeoutSeconds());
        await using var db = CreateContext(dataSource);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(CaptureIsolation, cancellationToken);

        Initiative[] initiatives = await db.Initiatives.AsNoTracking().ToArrayAsync(cancellationToken);
        Label[] labels = await db.Labels.AsNoTracking().ToArrayAsync(cancellationToken);
        MemoryGroup[] memoryGroups = await db.MemoryGroups.AsNoTracking().ToArrayAsync(cancellationToken);
        GroupDescription[] groupDescriptions = await db.GroupDescriptions.AsNoTracking().ToArrayAsync(cancellationToken);
        Memory[] memories = await db.Memories.AsNoTracking().ToArrayAsync(cancellationToken);
        MemoryVersion[] memoryVersions = await db.MemoryVersions.AsNoTracking().ToArrayAsync(cancellationToken);

        OperationReceipt[] receipts = await db.Database.SqlQueryRaw<OperationReceipt>(
            "SELECT operation_key AS \"OperationKey\", payload_hash AS \"PayloadHash\", operation_type AS \"OperationType\", result::text AS \"ResultJson\", committed_at AS \"CommittedAt\" FROM public.operation_receipt ORDER BY operation_key").ToArrayAsync(cancellationToken);

        SnapshotVertex[] vertices = await ReadMemoryVerticesAsync(db, cancellationToken);
        SnapshotEdge[] edges = await ReadMemoryEdgesAsync(db, cancellationToken);
        SnapshotTicketVertex[] ticketVertices = await ReadTicketVerticesAsync(db, cancellationToken);
        SnapshotTicketEdge[] ticketEdges = await ReadTicketEdgesAsync(db, cancellationToken);

        // Resolve the blob set while the database snapshot is still held, so the walk and the state
        // citing it describe the same moment (LADR-03). Committing first would let a blob be deleted
        // between commit and walk and be recorded missing though it existed at capture.
        SnapshotWalkResult walk = await WalkBlobsAsync(memoryVersions, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var capture = new SnapshotCapture(
            initiatives,
            labels,
            memoryGroups,
            groupDescriptions,
            memories,
            memoryVersions,
            vertices,
            edges,
            ticketVertices,
            ticketEdges,
            receipts);

        return new SnapshotCaptureResult(
            capture,
            walk,
            new SnapshotCounts(
                memories.Length,
                memoryVersions.Length,
                vertices.Length,
                edges.Length,
                walk.Blobs.Count,
                ticketVertices.Length,
                ticketEdges.Length,
                receipts.Length));
    }

    public async Task<bool> IsTargetEmptyAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Budgeted like the restore it gates. This is the pre-flight the operator runs by hand, so it
        // runs on the same corpus the restore will; counting rows unbudgeted meant the check could fail
        // on a large target for a reason the restore's own budget would have absorbed.
        await using NpgsqlDataSource dataSource =
            NpgsqlDataSourceFactory.Create(connectionString, RestoreStatementTimeoutSeconds());
        await using var db = CreateContext(dataSource);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(RestoreIsolation, cancellationToken);
        return await IsEmptyAsync(db, cancellationToken);
    }

    public async Task<RestoreResults> RestoreAsync(
        string connectionString,
        SnapshotCapture capture,
        SnapshotCounts expected,
        bool overrideNonEmpty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(expected);

        await using NpgsqlDataSource dataSource =
            NpgsqlDataSourceFactory.Create(connectionString, RestoreStatementTimeoutSeconds());
        await using var db = CreateContext(dataSource);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(RestoreIsolation, cancellationToken);

        // SET LOCAL, so the relaxed budget lives exactly as long as this transaction and no longer.
        // Placed before the emptiness check so a slow count on a large target is covered too. The
        // client-side half of the same budget is the data source's CommandTimeout above; this line
        // alone only raises the server's willingness to wait, which is not what the client obeys.
        await ExecuteNonQueryAsync(
            db,
            $"SET LOCAL statement_timeout = '{RestoreStatementTimeoutSeconds().ToString(CultureInfo.InvariantCulture)}s'",
            cancellationToken);

        var commitStore = new NpgsqlCorpusCommitStore(db);
        await commitStore.LockAsync(cancellationToken);
        if (!await IsEmptyAsync(db, cancellationToken) && !overrideNonEmpty)
        {
            throw new InvalidOperationException(
                "Restore target is not empty; overrideNonEmpty is required to clear it.");
        }

        await ClearStoresAsync(db, cancellationToken);
        await RestoreRelationalAsync(db, capture, cancellationToken);
        await RestoreVerticesAsync(db, capture, cancellationToken);
        await RestoreEdgesAsync(db, capture, cancellationToken);
        await RestoreTicketVerticesAsync(db, capture, cancellationToken);
        await RestoreTicketEdgesAsync(db, capture, cancellationToken);
        await ResetSequencesAsync(db, cancellationToken);
        foreach (OperationReceipt receipt in capture.OperationReceipts ?? [])
        {
            await commitStore.RecordAsync(receipt, cancellationToken);
        }

        int restoredReceipts = await CountAsync(db, "operation_receipt", cancellationToken);
        if (restoredReceipts != expected.OperationReceipts)
        {
            throw new InvalidDataException("Restored operation receipt count does not match the snapshot.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.corpus_state SET epoch = {Guid.NewGuid()}, revision = revision + 1 WHERE singleton", cancellationToken);

        // Counts are read back from the restored stores, never echoed from the capture: a Cypher
        // MATCH that finds no endpoint makes its CREATE a silent no-op, so only a read-back can see
        // a dropped edge. Reconcile before commit so a mismatch rolls back (LADR-05 / NFR-02).
        RestoreResults readBack = await ReadBackAsync(db, cancellationToken);
        if (!Closes(readBack, expected))
        {
            await transaction.RollbackAsync(cancellationToken);
            return readBack;
        }

        await transaction.CommitAsync(cancellationToken);
        return readBack with { Committed = true };
    }

    private static bool Closes(RestoreResults readBack, SnapshotCounts expected) =>
        readBack.Memories == expected.Memories
        && readBack.Versions == expected.Versions
        && readBack.Vertices == expected.Vertices
        && readBack.Edges == expected.Edges
        && readBack.TicketVertices == expected.TicketVertices
        && readBack.TicketEdges == expected.TicketEdges
        && readBack.TraversalPathCount == expected.Edges;

    private static async Task<RestoreResults> ReadBackAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken) =>
        new(
            await CountAsync(db, "memory", cancellationToken),
            await CountAsync(db, "memory_version", cancellationToken),
            await CountAsync(db, $"memory_graph.\"{AgeSession.VertexLabel}\"", cancellationToken),
            await CountAsync(db, $"memory_graph.\"{AgeSession.EdgeLabel}\"", cancellationToken),
            await CountAsync(db, "memory_graph.\"Ticket\"", cancellationToken),
            await CountAsync(db, "memory_graph.\"TICKET_PARENT\"", cancellationToken),
            await RunTraversalAsync(db, cancellationToken),
            Committed: false);

    private static async Task<int> CountAsync(
        SmoothAiProductContextMemoryDbContext db,
        string table,
        CancellationToken cancellationToken) =>
        checked((int)await ExecuteScalarLongAsync(db, $"SELECT count(*) FROM {table}", cancellationToken));

    private async Task<SnapshotWalkResult> WalkBlobsAsync(MemoryVersion[] versions, CancellationToken cancellationToken)
    {
        string[] cited = versions
            .Select(v => v.BlobAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var blobs = new List<SnapshotBlob>(cited.Length);
        int dangling = 0;
        foreach (string address in cited)
        {
            BlobContent? content = await blobStorage.GetAsync(address, cancellationToken);
            if (content is null)
            {
                dangling++;
                blobs.Add(new SnapshotBlob(address, SnapshotBlobState.Missing));
                continue;
            }

            await using (content)
            {
                using var buffer = new MemoryStream();
                await content.Content.CopyToAsync(buffer, cancellationToken);
                string actual = Sha256ContentAddress.Compute(buffer.ToArray());
                blobs.Add(new SnapshotBlob(
                    address,
                    string.Equals(actual, address, StringComparison.Ordinal)
                        ? SnapshotBlobState.Ok
                        : SnapshotBlobState.Mismatch));
            }
        }

        string[] catalog = await blobCatalog.ListAsync(cancellationToken);
        var citedSet = new HashSet<string>(cited, StringComparer.Ordinal);
        int orphans = catalog.Count(a => !citedSet.Contains(a));

        return new SnapshotWalkResult(blobs, dangling, orphans);
    }

    private static async Task RestoreRelationalAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        db.Initiatives.AddRange(capture.Initiatives);
        db.Labels.AddRange(capture.Labels);
        db.MemoryGroups.AddRange(capture.MemoryGroups);
        db.GroupDescriptions.AddRange(capture.GroupDescriptions);
        db.Memories.AddRange(capture.Memories);
        db.MemoryVersions.AddRange(capture.MemoryVersions);
        // Bounded batches. Unbounded, EF emitted the whole capture — every memory_version row of the
        // corpus — as one INSERT, whose cost grows with the archive and which no statement-timeout
        // raise makes cheap. A per-row cascade trigger on the clearing DELETE has the same shape of
        // problem and is bounded by the statement budget above rather than by batching.
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RestoreVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotVertex vertex in capture.Vertices)
        {
            string cypher = $"MERGE (n:{AgeSession.VertexLabel} {{memory_uuid: {Quote(vertex.MemoryUuid)}}})";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }
    }

    private static async Task RestoreEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotEdge edge in capture.Edges)
        {
            // Property predicates, not an inline map: `{memory_uuid: x}` compiles to `properties @>`
            // and sequentially scans the vertex table; `WHERE s.memory_uuid = x` is served by
            // ix_memory_vertex_uuid (PERSISTENCE_AGENTS non-negotiable). This runs once per captured
            // edge, so on a large corpus restore the inline form costs N full vertex scans.
            string cypher =
                $"MATCH (s:{AgeSession.VertexLabel}), (t:{AgeSession.VertexLabel}) " +
                $"WHERE s.memory_uuid = {Quote(edge.SourceUuid)} " +
                $"  AND t.memory_uuid = {Quote(edge.TargetUuid)} " +
                $"CREATE (s)-[:{AgeSession.EdgeLabel} {{relation: {CypherLiteral.Quote(edge.Relation)}, reason: {CypherLiteral.Quote(edge.Reason)}}}]->(t)";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }
    }

    private static async Task RestoreTicketVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotTicketVertex vertex in capture.TicketVertices)
        {
            string cypher = $"MERGE (t:Ticket {{provider: {CypherLiteral.Quote(vertex.Provider)}, key: {CypherLiteral.Quote(vertex.Key)}}})";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }
    }

    private static async Task RestoreTicketEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotTicketEdge edge in capture.TicketEdges)
        {
            string observed = edge.ObservedAt is null
                ? string.Empty
                : $", observedAt: {CypherLiteral.Quote(edge.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture))}";
            string props =
                $"reason: {CypherLiteral.Quote(edge.Reason)}, source: {CypherLiteral.Quote(edge.Source)}, " +
                $"recordedAt: {CypherLiteral.Quote(edge.RecordedAt.ToString("O", CultureInfo.InvariantCulture))}{observed}";
            // Predicate form, mirroring NpgsqlTicketGraph.MutationPredicate: the inline-map anchor
            // compiles to `properties @>` and scans the vertex table. Ticket provider/key equality
            // has dedicated HASH expression indexes, so the predicate form is index-served.
            string cypher =
                $"MATCH (c:Ticket), (p:Ticket) " +
                $"WHERE c.provider = {CypherLiteral.Quote(edge.Provider)} AND c.key = {CypherLiteral.Quote(edge.Key)} " +
                $"  AND p.provider = {CypherLiteral.Quote(edge.ParentProvider)} AND p.key = {CypherLiteral.Quote(edge.ParentKey)} " +
                $"CREATE (p)-[:TICKET_PARENT {{{props}}}]->(c)";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }
    }

    private static async Task<SnapshotVertex[]> ReadMemoryVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        string cypher = $"MATCH (n:{AgeSession.VertexLabel}) RETURN n.memory_uuid";
        await using NpgsqlCommand command = CypherCommand(db, cypher, ["uuid"]);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SnapshotVertex>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SnapshotVertex(Guid.Parse(AgtypeArrayReader.ReadAgtypeString(reader, 0))));
        }

        return rows.ToArray();
    }

    private static async Task<SnapshotEdge[]> ReadMemoryEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        string cypher = $"""
            MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel})
            RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
            """;
        await using NpgsqlCommand command = CypherCommand(db, cypher, ["src", "tgt", "rel", "reason"]);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SnapshotEdge>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SnapshotEdge(
                Guid.Parse(AgtypeArrayReader.ReadAgtypeString(reader, 0)),
                Guid.Parse(AgtypeArrayReader.ReadAgtypeString(reader, 1)),
                AgtypeArrayReader.ReadAgtypeString(reader, 2),
                AgtypeArrayReader.ReadAgtypeString(reader, 3)));
        }

        return rows.ToArray();
    }

    private static async Task<SnapshotTicketVertex[]> ReadTicketVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        string cypher = "MATCH (t:Ticket) RETURN t.provider, t.key";
        await using NpgsqlCommand command = CypherCommand(db, cypher, ["provider", "key"]);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SnapshotTicketVertex>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SnapshotTicketVertex(AgtypeArrayReader.ReadAgtypeString(reader, 0), AgtypeArrayReader.ReadAgtypeString(reader, 1)));
        }

        return rows.ToArray();
    }

    private static async Task<SnapshotTicketEdge[]> ReadTicketEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        // Bind p=parent, c=child to match the canonical TICKET_PARENT direction
        // (NpgsqlTicketGraph CREATE (p)-[:TICKET_PARENT]->(c)), so edge.Provider is the
        // child and edge.ParentProvider is the parent — the same mapping restore uses.
        string cypher = """
            MATCH (p:Ticket)-[e:TICKET_PARENT]->(c:Ticket)
            RETURN p.provider, p.key, c.provider, c.key, e.reason, e.source, e.recordedAt, e.observedAt
            """;
        await using NpgsqlCommand command = CypherCommand(
            db, cypher, ["pprovider", "pkey", "cprovider", "ckey", "reason", "source", "recorded", "observed"]);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SnapshotTicketEdge>();
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? observedAt = ReadOptionalAgtypeString(reader, 7) is { } observed
                ? DateTimeOffset.Parse(observed, CultureInfo.InvariantCulture)
                : null;
            rows.Add(new SnapshotTicketEdge(
                AgtypeArrayReader.ReadAgtypeString(reader, 2),
                AgtypeArrayReader.ReadAgtypeString(reader, 3),
                AgtypeArrayReader.ReadAgtypeString(reader, 0),
                AgtypeArrayReader.ReadAgtypeString(reader, 1),
                AgtypeArrayReader.ReadAgtypeString(reader, 4),
                AgtypeArrayReader.ReadAgtypeString(reader, 5),
                DateTimeOffset.Parse(AgtypeArrayReader.ReadAgtypeString(reader, 6), CultureInfo.InvariantCulture),
                observedAt));
        }

        return rows.ToArray();
    }

    private static async Task<bool> IsEmptyAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        // Every table ClearStoresAsync deletes must be guarded, so a target holding registry history
        // or a populated graph is refused rather than silently wiped. The one exception is
        // recall_feedback: it is telemetry about a corpus, not corpus content, and a target whose
        // only rows are feedback has no memory those rows could still describe. Counting it would
        // refuse a first restore into a database that has merely answered one query (a miss row).
        string[] relational = ["memory", "memory_version", "group_description", "memory_group", "operation_receipt"];
        foreach (string table in relational)
        {
            if (await ExecuteScalarLongAsync(db, $"SELECT count(*) FROM {table}", cancellationToken) > 0)
            {
                return false;
            }
        }

        // `initiative` and `label` are guarded by what the migrations did NOT seed. A fresh database
        // carries the `to-be-decided` initiative and the default facet labels, so counting every row
        // would refuse every first restore. Excluding those two tables outright — which is what this
        // used to do — is the other half of the same coin: ClearStoresAsync deletes both
        // unconditionally, so a target holding one custom label and no memories at all was judged
        // empty and the label was destroyed by a restore the operator never overrode. The seed set is
        // the definition of "not operator content", read from the same constants HasData uses rather
        // than restated, and both names arrive as parameters: no value reaches the statement text.
        if (await ExecuteScalarLongAsync(
                db,
                "SELECT count(*) FROM label WHERE name <> ALL(@seedFacets)",
                cancellationToken,
                new NpgsqlParameter("seedFacets", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = LabelConfiguration.SeedFacets,
                }) > 0)
        {
            return false;
        }

        if (await ExecuteScalarLongAsync(
                db,
                "SELECT count(*) FROM initiative WHERE name <> @defaultInitiative",
                cancellationToken,
                new NpgsqlParameter("defaultInitiative", NpgsqlDbType.Text)
                {
                    Value = InitiativeConfiguration.DefaultInitiativeName,
                }) > 0)
        {
            return false;
        }

        string[] graph = ["Memory", "LINKS", "TICKET_PARENT", "Ticket"];
        foreach (string label in graph)
        {
            if (await ExecuteScalarLongAsync(db, $"SELECT count(*) FROM memory_graph.\"{label}\"", cancellationToken) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task ClearStoresAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(db, "SET LOCAL app.allow_history_delete = 'true'", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_version", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM group_description", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_group", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM label", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM initiative", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_graph.\"LINKS\"", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_graph.\"TICKET_PARENT\"", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_graph.\"Memory\"", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM memory_graph.\"Ticket\"", cancellationToken);

        // Recall feedback is excluded from the archive (HLD-004: disposable tuning telemetry), so a
        // restore can never bring the matching rows back. memory_uuid has no FK, so rows left behind
        // would cite memories of the corpus being replaced and skew never-recalled and miss-rate
        // against the restored one. Cleared inside the same transaction, so a rollback keeps them.
        await ExecuteNonQueryAsync(db, "DELETE FROM recall_feedback", cancellationToken);
        await ExecuteNonQueryAsync(db, "DELETE FROM operation_receipt", cancellationToken);
    }

    private static async Task ResetSequencesAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        string[] tables = ["initiative", "label", "memory_group", "group_description", "memory", "memory_version"];
        foreach (string table in tables)
        {
            string sql = $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), COALESCE((SELECT MAX(id) FROM {table}), 0) + 1, false)";
            await ExecuteNonQueryAsync(db, sql, cancellationToken);
        }
    }

    private static async Task ExecuteCypherAsync(
        SmoothAiProductContextMemoryDbContext db,
        string cypher,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = CypherCommand(db, cypher, ["v"]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> RunTraversalAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        // Bounded traversal over restored edges; counts as a regression guard that the graph is
        // connected post-restore (NFR-02). Non-zero return is expected for a seeded corpus.
        string cypher = $"MATCH (s:{AgeSession.VertexLabel})-[e:{AgeSession.EdgeLabel}]->(t:{AgeSession.VertexLabel}) RETURN count(e)";
        await using NpgsqlCommand command = CypherCommand(db, cypher, ["count"]);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
        int suffix = raw.IndexOf("::", StringComparison.Ordinal);
        if (suffix >= 0)
        {
            raw = raw[..suffix];
        }

        return int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out int count) ? count : 0;
    }

    private static NpgsqlCommand CypherCommand(
        SmoothAiProductContextMemoryDbContext db,
        string cypher,
        IReadOnlyList<string> columns)
    {
        string selectList = string.Join(", ", columns.Select(c => $"{c}::text"));
        string asList = string.Join(", ", columns.Select(c => $"{c} ag_catalog.agtype"));
        string sql = $"""
            SELECT {selectList}
            FROM ag_catalog.cypher('{AgeSession.GraphName}', {CypherLiteral.DollarWrap(cypher)}) AS ({asList});
            """;
        return Command(db, sql);
    }

    private static NpgsqlCommand Command(SmoothAiProductContextMemoryDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        // No per-command timeout is set, so a raw statement inherits the connection's default — which
        // is the budget, because every data source this class builds is built with it. One lever for
        // both paths: EF batches and these raw statements read the same connection default, so the
        // budget cannot govern one and miss the other. A command carrying its own value would be a
        // second copy of the number to keep in step.
        return new NpgsqlCommand(sql, connection, transaction);
    }

    private static SmoothAiProductContextMemoryDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.UseSmoothAiProductContextMemoryHistory();
                // Bounds every EF batch this context emits. Restore is the only writer that uses it,
                // and unbatched it emitted the whole capture's memory_version rows as one INSERT —
                // a statement whose cost grows with the archive, which no statement-timeout raise
                // makes cheap. Capture only reads, so the cap costs it nothing.
                npgsql.MaxBatchSize(RestoreBatchSize);
            })
            .Options;
        return new SmoothAiProductContextMemoryDbContext(options);
    }

    /// <summary>
    /// The statement budget for capture and restore, overridable by configuration. A configured value
    /// outside 1–86400 seconds is rejected rather than replaced by the default: silently substituting
    /// 900 s for an operator who asked for 3600 s gives them a restore that rolls back at 900 s and no
    /// signal that the setting was ignored, which is the same invisible-budget defect the client-side
    /// half of this budget had. The rejection is shape-only (NFR-05) and names the setting, not the
    /// value that reached it.
    /// </summary>
    private int RestoreStatementTimeoutSeconds() =>
        _configuredRestoreTimeoutSeconds is not { } configured
            ? DefaultRestoreStatementTimeoutSeconds
            : configured is > 0 and <= MaxRestoreStatementTimeoutSeconds
                ? configured
                : throw new InvalidOperationException(
                    $"Snapshot:RestoreStatementTimeoutSeconds must be between 1 and {MaxRestoreStatementTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds; the value configured is not usable.");

    private static async Task<long> ExecuteScalarLongAsync(
        SmoothAiProductContextMemoryDbContext db,
        string sql,
        CancellationToken cancellationToken,
        NpgsqlParameter? parameter = null)
    {
        await using NpgsqlCommand command = Command(db, sql);
        if (parameter is not null)
        {
            command.Parameters.Add(parameter);
        }

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQueryAsync(
        SmoothAiProductContextMemoryDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(db, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(Guid uuid) => CypherLiteral.Quote(uuid.ToString("D"));

    private static string? ReadOptionalAgtypeString(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string raw = reader.GetString(ordinal);
        return string.Equals(raw, "null", StringComparison.Ordinal) ? null : AgtypeArrayReader.ReadAgtypeString(reader, ordinal);
    }
}
