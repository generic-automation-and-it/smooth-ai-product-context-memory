using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.Abstractions.Snapshot;

/// <summary>
/// The captured database state that defines corpus membership (LADR-03): every relational row,
/// every graph vertex and edge, plus the set of blob addresses the captured state cites. The
/// handler walks these addresses through <see cref="IBlobStorage"/> to build the blob set.
/// </summary>
public sealed record SnapshotCapture(
    IReadOnlyList<Initiative> Initiatives,
    IReadOnlyList<Label> Labels,
    IReadOnlyList<MemoryGroup> MemoryGroups,
    IReadOnlyList<GroupDescription> GroupDescriptions,
    IReadOnlyList<Memory> Memories,
    IReadOnlyList<MemoryVersion> MemoryVersions,
    IReadOnlyList<SnapshotVertex> Vertices,
    IReadOnlyList<SnapshotEdge> Edges,
    IReadOnlyList<SnapshotTicketVertex> TicketVertices,
    IReadOnlyList<SnapshotTicketEdge> TicketEdges);

/// <summary>A <c>Memory</c> graph vertex — carries only its <c>memory_uuid</c> (LADR-02, HLD-003).</summary>
public sealed record SnapshotVertex(Guid MemoryUuid);

/// <summary>A <c>:LINKS</c> memory edge.</summary>
public sealed record SnapshotEdge(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

/// <summary>A <c>Ticket</c> graph vertex — carries only exact <c>provider</c> and <c>key</c>.</summary>
public sealed record SnapshotTicketVertex(string Provider, string Key);

/// <summary>A <c>:TICKET_PARENT</c> ticket edge.</summary>
public sealed record SnapshotTicketEdge(
    string Provider, string Key, string ParentProvider, string ParentKey,
    string Reason, string Source, DateTimeOffset RecordedAt, DateTimeOffset? ObservedAt);
