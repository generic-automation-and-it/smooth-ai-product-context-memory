using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Abstractions.Snapshot;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Storage;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence;

/// <summary>
/// Captures and restores both stores over one data source built from the supplied connection string,
/// inside a single transaction so relational rows, AGE vertices/edges and the blob-reference set
/// describe the same moment. The graph is outside the EF model, so it is read/written via raw
/// <c>ag_catalog.cypher</c> on the same connection as the relational SQL (LADR-03 / LADR-05 / LADR-07).
/// </summary>
public sealed class NpgsqlSnapshotRepository(IBlobStorage blobStorage, IBlobCatalog blobCatalog)
    : ISnapshotRepository
{
    private const IsolationLevel CaptureIsolation = IsolationLevel.RepeatableRead;
    private const IsolationLevel RestoreIsolation = IsolationLevel.ReadCommitted;

    public async Task<SnapshotCaptureResult> CaptureAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(connectionString);
        await using var db = CreateContext(dataSource);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(CaptureIsolation, cancellationToken);

        Initiative[] initiatives = await db.Initiatives.AsNoTracking().ToArrayAsync(cancellationToken);
        Label[] labels = await db.Labels.AsNoTracking().ToArrayAsync(cancellationToken);
        MemoryGroup[] memoryGroups = await db.MemoryGroups.AsNoTracking().ToArrayAsync(cancellationToken);
        GroupDescription[] groupDescriptions = await db.GroupDescriptions.AsNoTracking().ToArrayAsync(cancellationToken);
        Memory[] memories = await db.Memories.AsNoTracking().ToArrayAsync(cancellationToken);
        MemoryVersion[] memoryVersions = await db.MemoryVersions.AsNoTracking().ToArrayAsync(cancellationToken);

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
            ticketEdges);

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
                ticketEdges.Length));
    }

    public async Task<bool> IsTargetEmptyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(connectionString);
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

        await using NpgsqlDataSource dataSource = NpgsqlDataSourceFactory.Create(connectionString);
        await using var db = CreateContext(dataSource);
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(RestoreIsolation, cancellationToken);

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
            string cypher =
                $"MATCH (s:{AgeSession.VertexLabel} {{memory_uuid: {Quote(edge.SourceUuid)}}}), " +
                $"(t:{AgeSession.VertexLabel} {{memory_uuid: {Quote(edge.TargetUuid)}}}) " +
                $"CREATE (s)-[:{AgeSession.EdgeLabel} {{relation: {Quote(edge.Relation)}, reason: {Quote(edge.Reason)}}}]->(t)";
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
            string cypher = $"MERGE (t:Ticket {{provider: {Quote(vertex.Provider)}, key: {Quote(vertex.Key)}}})";
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
                : $", observedAt: {Quote(edge.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture))}";
            string props =
                $"reason: {Quote(edge.Reason)}, source: {Quote(edge.Source)}, " +
                $"recordedAt: {Quote(edge.RecordedAt.ToString("O", CultureInfo.InvariantCulture))}{observed}";
            string cypher =
                $"MATCH (c:Ticket {{provider: {Quote(edge.Provider)}, key: {Quote(edge.Key)}}}), " +
                $"(p:Ticket {{provider: {Quote(edge.ParentProvider)}, key: {Quote(edge.ParentKey)}}}) " +
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
            rows.Add(new SnapshotVertex(Guid.Parse(ReadAgtypeString(reader, 0))));
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
                Guid.Parse(ReadAgtypeString(reader, 0)),
                Guid.Parse(ReadAgtypeString(reader, 1)),
                ReadAgtypeString(reader, 2),
                ReadAgtypeString(reader, 3)));
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
            rows.Add(new SnapshotTicketVertex(ReadAgtypeString(reader, 0), ReadAgtypeString(reader, 1)));
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
                ReadAgtypeString(reader, 2),
                ReadAgtypeString(reader, 3),
                ReadAgtypeString(reader, 0),
                ReadAgtypeString(reader, 1),
                ReadAgtypeString(reader, 4),
                ReadAgtypeString(reader, 5),
                DateTimeOffset.Parse(ReadAgtypeString(reader, 6), CultureInfo.InvariantCulture),
                observedAt));
        }

        return rows.ToArray();
    }

    private static async Task<bool> IsEmptyAsync(
        SmoothAiProductContextMemoryDbContext db,
        CancellationToken cancellationToken)
    {
        // Every table ClearStoresAsync deletes must be guarded, so a target holding registry history
        // or a populated graph is refused rather than silently wiped. `initiative` and `label` are
        // excluded: migrations seed the `to-be-decided` initiative and default facet labels on a
        // fresh database, so a freshly-migrated target carries those rows and must still count as
        // empty for a first restore.
        string[] relational = ["memory", "memory_version", "group_description", "memory_group"];
        foreach (string table in relational)
        {
            if (await ExecuteScalarLongAsync(db, $"SELECT count(*) FROM {table}", cancellationToken) > 0)
            {
                return false;
            }
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
        return new NpgsqlCommand(sql, connection, transaction);
    }

    private static SmoothAiProductContextMemoryDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<SmoothAiProductContextMemoryDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.UseSmoothAiProductContextMemoryHistory())
            .Options;
        return new SmoothAiProductContextMemoryDbContext(options);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SmoothAiProductContextMemoryDbContext db,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(db, sql);
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

    private static string Quote(string value) => CypherLiteral.Quote(value);

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

    private static string? ReadOptionalAgtypeString(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        string raw = reader.GetString(ordinal);
        return string.Equals(raw, "null", StringComparison.Ordinal) ? null : ReadAgtypeString(reader, ordinal);
    }
}
