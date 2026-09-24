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

    public async Task<RestoreResults> RestoreAsync(
        string connectionString,
        SnapshotCapture capture,
        bool overrideNonEmpty,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);

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

        int vertices = await RestoreVerticesAsync(db, capture, cancellationToken);
        int edges = await RestoreEdgesAsync(db, capture, cancellationToken);
        int ticketVertices = await RestoreTicketVerticesAsync(db, capture, cancellationToken);
        int ticketEdges = await RestoreTicketEdgesAsync(db, capture, cancellationToken);

        await ResetSequencesAsync(db, cancellationToken);
        int traversalPathCount = await RunTraversalAsync(db, cancellationToken);

        // The restore should not commit if a store write failed — the reconciliation runs after
        // commit but the traversal and the row writes happened in one transaction, so a throw here
        // rolls the whole restore back (no partial success, LADR-05).
        await transaction.CommitAsync(cancellationToken);

        int objects = capture.MemoryVersions
            .Select(v => v.BlobAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new RestoreResults(
            capture.Memories.Count,
            capture.MemoryVersions.Count,
            vertices,
            edges,
            objects,
            ticketVertices,
            ticketEdges,
            traversalPathCount);
    }

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

    private static async Task<int> RestoreVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotVertex vertex in capture.Vertices)
        {
            string cypher = $"MERGE (n:{AgeSession.VertexLabel}) {{memory_uuid: {Quote(vertex.MemoryUuid)}}}";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }

        return capture.Vertices.Count;
    }

    private static async Task<int> RestoreEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotEdge edge in capture.Edges)
        {
            string cypher = $$"""
                MATCH (s:{{AgeSession.VertexLabel}} {memory_uuid: {{Quote(edge.SourceUuid)}}}),
                      (t:{{AgeSession.VertexLabel}} {memory_uuid: {{Quote(edge.TargetUuid)}}})
                CREATE (s)-[:{{AgeSession.EdgeLabel}} {relation: {{Quote(edge.Relation)}}, reason: {{Quote(edge.Reason)}}}]->(t)
                """;
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }

        return capture.Edges.Count;
    }

    private static async Task<int> RestoreTicketVerticesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotTicketVertex vertex in capture.TicketVertices)
        {
            string cypher = $"MERGE (t:Ticket) {{provider: {Quote(vertex.Provider)}, key: {Quote(vertex.Key)}}}";
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }

        return capture.TicketVertices.Count;
    }

    private static async Task<int> RestoreTicketEdgesAsync(
        SmoothAiProductContextMemoryDbContext db,
        SnapshotCapture capture,
        CancellationToken cancellationToken)
    {
        foreach (SnapshotTicketEdge edge in capture.TicketEdges)
        {
            string observed = edge.ObservedAt is null
                ? string.Empty
                : $", observedAt: {Quote(edge.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture))}";
            string cypher = $$"""
                MATCH (c:Ticket {provider: {{Quote(edge.Provider)}}, key: {{Quote(edge.Key)}}}),
                      (p:Ticket {provider: {{Quote(edge.ParentProvider)}}, key: {{Quote(edge.ParentKey)}}})
                CREATE (p)-[:TICKET_PARENT {reason: {{Quote(edge.Reason)}}, source: {{Quote(edge.Source)}},
                    recordedAt: {{Quote(edge.RecordedAt.ToString("O", CultureInfo.InvariantCulture))}}{{observed}}]->(c)
                """;
            await ExecuteCypherAsync(db, cypher, cancellationToken);
        }

        return capture.TicketEdges.Count;
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
        string cypher = """
            MATCH (c:Ticket)-[e:TICKET_PARENT]->(p:Ticket)
            RETURN c.provider, c.key, p.provider, p.key, e.reason, e.source, e.recordedAt, e.observedAt
            """;
        await using NpgsqlCommand command = CypherCommand(
            db, cypher, ["cprovider", "ckey", "pprovider", "pkey", "reason", "source", "recorded", "observed"]);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SnapshotTicketEdge>();
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? observedAt = ReadOptionalAgtypeString(reader, 7) is { } observed
                ? DateTimeOffset.Parse(observed, CultureInfo.InvariantCulture)
                : null;
            rows.Add(new SnapshotTicketEdge(
                ReadAgtypeString(reader, 0),
                ReadAgtypeString(reader, 1),
                ReadAgtypeString(reader, 2),
                ReadAgtypeString(reader, 3),
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
        long memories = await ExecuteScalarLongAsync(db, "SELECT count(*) FROM memory", cancellationToken);
        if (memories > 0)
        {
            return false;
        }

        long vertices = await ExecuteScalarLongAsync(db, "SELECT count(*) FROM memory_graph.\"Memory\"", cancellationToken);
        return vertices == 0;
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
