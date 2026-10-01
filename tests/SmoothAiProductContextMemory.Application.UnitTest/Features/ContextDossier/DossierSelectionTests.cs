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

    [Theory]
    [InlineData("decision", "approved", "repo")]
    [InlineData("  ", "proposed", null)]
    [InlineData(null, " ", "program")]
    public async Task Every_stage_query_carries_the_one_selection_filter(string? kind, string? status, string? scope)
    {
        Guid anchorUuid = Guid.NewGuid();
        var ticketGraph = new RecordingTicketGraph(
            [Memory(anchorUuid)], new TicketTraversalDisclosure(2, 200, 200, false, false, false));
        var search = new RecordingSearch([Memory(anchorUuid)]);
        var traversal = new RecordingTraversal();
        DossierAnchor anchor = Anchor(
            repo: "org/repo", ticket: Ticket, kind: kind, status: status, scopeDimension: scope,
            asOf: new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.FromHours(2)));

        await DossierSelection.ResolveAsync(
            search, traversal, ticketGraph, new EmptyGraph(), anchor, TestContext.Current.CancellationToken);

        SelectionFilter filter = SelectionFilter.From(anchor);
        filter.Kind.ShouldBe(string.IsNullOrWhiteSpace(kind) ? null : kind);
        filter.Status.ShouldBe(string.IsNullOrWhiteSpace(status) ? null : status);
        filter.ExcludeProposed.ShouldBeTrue();
        filter.AsOf.ShouldBe(anchor.AsOf);

        TicketTraversalQuery ticketQuery = ticketGraph.Query.ShouldNotBeNull();
        ticketQuery.Anchor.ShouldBe(Ticket);
        ticketQuery.Kind.ShouldBe(filter.Kind);
        ticketQuery.Status.ShouldBe(filter.Status);
        ticketQuery.ExcludeProposed.ShouldBe(filter.ExcludeProposed);
        ticketQuery.RequiredScopeDimension.ShouldBe(filter.RequiredScopeDimension);
        ticketQuery.HiddenDimensions.ShouldBe(filter.HiddenDimensions);

        MemorySearchCriteria criteria = search.Criteria.ShouldNotBeNull();
        criteria.Kind.ShouldBe(filter.Kind);
        criteria.Status.ShouldBe(filter.Status);
        criteria.ExcludeProposed.ShouldBe(filter.ExcludeProposed);
        criteria.RequiredScopeDimension.ShouldBe(filter.RequiredScopeDimension);
        criteria.ExcludedScopeDimensions.ShouldBe(filter.ExcludedScopeDimensions);
        criteria.AsOf.ShouldBe(filter.AsOf);
        criteria.UuidFilter.ShouldBe([anchorUuid]);
        criteria.Repo.ShouldBe("org/repo");

        MemoryWidenQuery widen = traversal.Query.ShouldNotBeNull();
        widen.Kind.ShouldBe(filter.Kind);
        widen.Status.ShouldBe(filter.Status);
        widen.ExcludeProposed.ShouldBe(filter.ExcludeProposed);
        widen.RequiredScopeDimension.ShouldBe(filter.RequiredScopeDimension);
        widen.HiddenDimensions.ShouldBe(filter.HiddenDimensions);
        widen.AsOf.ShouldBe(filter.AsOf);
        widen.SourceUuids.ShouldBe([anchorUuid]);
        widen.MaxDepth.ShouldBe(anchor.WidenDepth);
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
