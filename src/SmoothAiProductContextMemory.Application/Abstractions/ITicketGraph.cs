using SmoothAiProductContextMemory.Application.Common.Models;

namespace SmoothAiProductContextMemory.Application.Abstractions;

public sealed record TicketIdentity(string Provider, string Key);

public sealed record TicketParentChange(
    TicketIdentity Child,
    TicketIdentity? Parent,
    TicketIdentity? ExpectedParent,
    string Reason,
    string Source,
    DateTimeOffset? ObservedAt = null);

public sealed record TicketHierarchyHop(
    TicketIdentity Parent,
    TicketIdentity Child,
    string Reason,
    string Source,
    DateTimeOffset? ObservedAt,
    DateTimeOffset RecordedAt);

public sealed record TicketHierarchyPath(int Depth, IReadOnlyList<TicketHierarchyHop> Hops);

public sealed record TicketTraversalQuery
{
    public required TicketIdentity Anchor { get; init; }

    public required int MaxDepth { get; init; }

    public TraversalDirection Direction { get; init; } = TraversalDirection.Outbound;

    public string? RequiredScopeDimension { get; init; }

    public IReadOnlyList<string> HiddenDimensions { get; init; } = ["program"];

    public string? Kind { get; init; }

    public int PathLimit { get; init; } = 50;

    public int MemoryLimit { get; init; } = 50;
}

public sealed record TicketTraversalDisclosure(
    int MaxDepth,
    int PathLimit,
    int MemoryLimit,
    bool DepthLimitReached,
    bool PathLimitReached,
    bool MemoryLimitReached)
{
    public string HierarchyCoverage =>
        "Only practitioner-declared hierarchy was followed. Undeclared upstream hierarchy was not followed; upstream freshness and completeness are unverified.";
}

public sealed record TicketTraversalResult(
    IReadOnlyList<TicketHierarchyPath> Paths,
    IReadOnlyList<CheapMemory> Items,
    TicketTraversalDisclosure Disclosure);

public interface ITicketGraph
{
    /// <summary>Serializes group-ticket and hierarchy changes within the caller's transaction.</summary>
    Task LockAsync(CancellationToken cancellationToken);

    /// <summary>Changes a declared parent atomically; an identical state is a no-op only when the expected parent matches.</summary>
    Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken);

    Task<TicketTraversalResult> TraverseAsync(TicketTraversalQuery query, CancellationToken cancellationToken);
}
