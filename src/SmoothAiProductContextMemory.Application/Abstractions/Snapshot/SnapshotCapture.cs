using NodaTime;
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
    IReadOnlyList<SnapshotTicketEdge> TicketEdges,
    IReadOnlyList<SnapshotAboutEdge> AboutEdges,
    IReadOnlyList<SnapshotProductVertex> ProductVertices,
    IReadOnlyList<SnapshotNamedVertex> InitiativeVertices,
    IReadOnlyList<SnapshotNamedVertex> RepoVertices,
    IReadOnlyList<SnapshotProductInitiativeEdge> ProductInitiativeEdges,
    IReadOnlyList<SnapshotProductRepoEdge> ProductRepoEdges);

/// <summary>A <c>Memory</c> graph vertex — carries only its <c>memory_uuid</c> (LADR-02, HLD-003).</summary>
public sealed record SnapshotVertex(Guid MemoryUuid);

/// <summary>A <c>:LINKS</c> memory edge.</summary>
public sealed record SnapshotEdge(Guid SourceUuid, Guid TargetUuid, string Relation, string Reason);

/// <summary>A <c>Ticket</c> graph vertex — carries only exact <c>provider</c> and <c>key</c>.</summary>
public sealed record SnapshotTicketVertex(string Provider, string Key);

/// <summary>A <c>:TICKET_PARENT</c> ticket edge.</summary>
public sealed record SnapshotTicketEdge(
    string Provider, string Key, string ParentProvider, string ParentKey,
    string Reason, string Source, Instant RecordedAt, Instant? ObservedAt);

/// <summary>An <c>:ABOUT</c> memory → ticket reference edge (HLD-003 LADR-09).</summary>
public sealed record SnapshotAboutEdge(
    Guid MemoryUuid, string Provider, string Key, string Reason, Instant DeclaredAt);

/// <summary>A <c>Product</c> graph vertex (HLD-003 LADR-10).</summary>
public sealed record SnapshotProductVertex(string Name, string? Description, string Status);

/// <summary>An identity-only <c>Initiative</c> or <c>Repo</c> graph vertex — carries only <c>name</c>.</summary>
public sealed record SnapshotNamedVertex(string Name);

/// <summary>A <c>:HAS_INITIATIVE</c> edge from a product to an initiative.</summary>
public sealed record SnapshotProductInitiativeEdge(
    string ProductName, string InitiativeName, string Reason, string Source, Instant DeclaredAt);

/// <summary>A <c>:HAS_REPO</c> edge from a product to a repo.</summary>
public sealed record SnapshotProductRepoEdge(
    string ProductName, string RepoName, string Reason, string Source, Instant DeclaredAt);
