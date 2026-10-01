using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.ContextDossier;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.UnitTest.Features.ContextDossier;

public class DossierSelectionTests
{
    private static readonly DateTimeOffset ValidFrom = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TicketIdentity Ticket = new("github", "org/repo#7");

    [Fact]
    public async Task Ticket_disclosure_survives_an_anchor_search_that_matches_nothing()
    {
        var ticketGraph = new RecordingTicketGraph(
            [Memory(Guid.NewGuid())],
            new TicketTraversalDisclosure(2, 50, 50, DepthLimitReached: true, PathLimitReached: false, MemoryLimitReached: true));
        var search = new RecordingSearch([]);

        DossierSelectionResult result = await DossierSelection.ResolveAsync(
            search, new RecordingTraversal(), ticketGraph, new EmptyGraph(),
            Anchor(repo: "elsewhere", ticket: Ticket), TestContext.Current.CancellationToken);

        result.Selected.ShouldBeEmpty();
        result.DepthLimitReached.ShouldBeTrue();
        result.LimitReached.ShouldBeTrue();
    }

    private static DossierAnchor Anchor(
        string? repo = null,
        TicketIdentity? ticket = null,
        string? kind = null,
        string? status = null,
        string? scopeDimension = null,
        DateTimeOffset? asOf = null) =>
        new(repo, null, ticket, [], kind, status, scopeDimension, false, asOf, 2, DossierDefaults.ItemLimit);

    private static CheapMemory Memory(Guid uuid) =>
        new(
            Uuid: uuid,
            GroupUuid: Guid.NewGuid(),
            Name: "name",
            Description: "description",
            Statement: "statement",
            ContentSummary: "summary",
            Kind: MemoryVersion.KindValue.Decision,
            Facets: [],
            Tags: [],
            Status: MemoryVersion.MemoryVersionStatus.Approved,
            Confidence: 80,
            ScopeDimension: "repo",
            ScopeIdentifier: "org/repo",
            ValidFrom: ValidFrom,
            ValidUntil: null,
            Version: 1,
            IsCurrent: true,
            Sources: [],
            CreatedOn: ValidFrom);

    private sealed class RecordingSearch(IReadOnlyList<CheapMemory> result) : IMemorySearch
    {
        public MemorySearchCriteria? Criteria { get; private set; }

        public Task<IReadOnlyList<CheapMemory>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken)
        {
            Criteria = criteria;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingTraversal : IMemoryTraversal
    {
        public MemoryWidenQuery? Query { get; private set; }

        public Task<IReadOnlyList<MemoryPath>> FindPathsAsync(MemoryPathQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryWidenResult> WidenAsync(MemoryWidenQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult(new MemoryWidenResult([], false, false, false));
        }
    }

    private sealed class RecordingTicketGraph(
        IReadOnlyList<CheapMemory> items,
        TicketTraversalDisclosure disclosure) : ITicketGraph
    {
        public TicketTraversalQuery? Query { get; private set; }

        public Task LockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ChangeParentAsync(TicketParentChange change, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TicketTraversalResult> TraverseAsync(TicketTraversalQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult(new TicketTraversalResult([], items, disclosure));
        }
    }

    private sealed class EmptyGraph : IMemoryGraph
    {
        public Task<bool> ExistsAsync(Guid sourceUuid, Guid targetUuid, string relation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CreateAsync(Guid sourceUuid, Guid targetUuid, string relation, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryRelationship>> ListAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryRelationship>> ListTouchingAsync(Guid uuid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryRelationship>> ListEdgesAsync(IReadOnlyCollection<Guid> uuids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRelationship>>([]);
    }
}
