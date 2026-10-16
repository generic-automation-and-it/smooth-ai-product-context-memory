using NodaTime;
using Microsoft.Extensions.Logging;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Runs against the real provider search so the predicates are proven as SQL. An in-memory filter
/// would pass while the database did something else.
/// </summary>
public sealed class QueryMemoriesHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Open_search_omits_program_and_proposed()
    {
        var product = TestEntities.NewGroup();
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.AddRange(product, program);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(product.Uuid, "Product fact"), Ct);
        await set.Handle(Proposed(product.Uuid, "Proposed fact"), Ct);
        await set.Handle(Approved(program.Uuid, "Program fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(Query(), Ct);

        response.Items.Select(i => i.Description).ShouldBe(["Product fact"]);
        response.Items[0].ScopeDimension.ShouldBe(MemoryGroup.ScopeDimensionValue.Product);
    }

    [Fact]
    public async Task Explicit_program_scope_returns_program()
    {
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.Add(program);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(program.Uuid, "Program fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(
            Query() with { ScopeDimension = MemoryGroup.ScopeDimensionValue.Program },
            Ct);

        response.Items.Select(i => i.Description).ShouldBe(["Program fact"]);
    }

    [Fact]
    public async Task Full_text_matches_subject_and_claim()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(group.Uuid, "The storage engine", "We chose PostgreSQL"), Ct);
        await NewSet().Handle(Approved(group.Uuid, "The queue", "We chose RabbitMQ"), Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query() with { Query = "storage" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["The storage engine"]);

        // Claim text is part of the search surface, not just the subject.
        (await query.Handle(Query() with { Query = "RabbitMQ" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["The queue"]);

        (await query.Handle(Query() with { Query = "nothing-matches-this" }, Ct))
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Facets_and_tags_match_any_by_default()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Request storage = Approved(group.Uuid, "Storage fact");
        storage = storage with { Items = [storage.Items[0] with { Facets = ["storage"], Tags = ["adr"] }] };
        await NewSet().Handle(storage, Ct);

        SetMemories.Request domain = Approved(group.Uuid, "Domain fact");
        domain = domain with { Items = [domain.Items[0] with { Facets = ["domain-model"], Tags = [] }] };
        await NewSet().Handle(domain, Ct);

        QueryMemories.Handler query = NewQuery();

        // Recall is the union: a row carrying any of the batch's facets must come back.
        (await query.Handle(Query() with { Facets = ["storage", "domain-model"] }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Storage fact", "Domain fact"], ignoreOrder: true);

        (await query.Handle(Query() with { Tags = ["adr"] }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Storage fact"]);

        (await query.Handle(Query() with { Facets = ["absent", "also-absent"] }, Ct))
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Facets_and_tags_narrow_by_containment_in_all_mode()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Request tagged = Approved(group.Uuid, "Tagged fact");
        tagged = tagged with { Items = [tagged.Items[0] with { Facets = ["architecture", "storage"], Tags = ["adr"] }] };
        await NewSet().Handle(tagged, Ct);

        SetMemories.Request other = Approved(group.Uuid, "Other fact");
        other = other with { Items = [other.Items[0] with { Facets = ["process"], Tags = [] }] };
        await NewSet().Handle(other, Ct);

        QueryMemories.Handler query = NewQuery();

        // "all" keeps the airtight narrowing form: only rows carrying every requested facet.
        (await query.Handle(Query() with { Facets = ["architecture", "storage"], FacetMatchMode = FacetMatchModeValue.All }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Tagged fact"]);

        (await query.Handle(Query() with { Facets = ["architecture", "absent"], FacetMatchMode = FacetMatchModeValue.All }, Ct))
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task As_of_excludes_claims_outside_their_validity()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Instant now = SystemClock.Instance.GetCurrentInstant();
        SetMemories.Request expired = Approved(group.Uuid, "Expired fact");
        expired = expired with
        {
            Items = [expired.Items[0] with { ValidFrom = now.Plus(Duration.FromDays(-10)), ValidUntil = now.Plus(Duration.FromDays(-5)) }]
        };
        await NewSet().Handle(expired, Ct);

        SetMemories.Request live = Approved(group.Uuid, "Live fact");
        live = live with { Items = [live.Items[0] with { ValidFrom = now.Plus(Duration.FromDays(-1)), ValidUntil = null }] };
        await NewSet().Handle(live, Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query() with { AsOf = now }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Live fact"]);

        (await query.Handle(Query() with { AsOf = now.Plus(Duration.FromDays(-7)) }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Expired fact"]);

        // No as-of means no temporal narrowing.
        (await query.Handle(Query(), Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Current_only_default_hides_superseded_versions()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        Guid uuid = (await set.Handle(Approved(group.Uuid, "Versioned fact", "Claim 1"), Ct)).Items[0].Uuid!.Value;
        SetMemories.Request bump = Approved(group.Uuid, "Versioned fact", "Claim 2");
        await set.Handle(bump with { Items = [bump.Items[0] with { Uuid = uuid }] }, Ct);

        QueryMemories.Handler query = NewQuery();

        (await query.Handle(Query(), Ct)).Items.Select(i => i.Statement).ShouldBe(["Claim 2"]);
        (await query.Handle(Query() with { CurrentOnly = false }, Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AsOf_does_not_reconstruct_the_version_that_was_current_then()
    {
        // The one combination that was missing: a multi-version memory *and* an AsOf, under the
        // default CurrentOnly. An experiment was tried and reverted in CI review that dropped
        // CurrentOnly when AsOf was set and closed the superseded window — and nothing here would
        // have failed, because every AsOf test so far used a single-version memory, where
        // CurrentOnly makes no difference. The documented model is that AsOf filters validity
        // windows on the versions the query already returns; it does not rewind.
        //
        // A rewind is not merely unwanted, it is unavailable: memory_version is append-only and its
        // trigger admits only an is_current flip, so closing the superseded version's ValidUntil
        // threw ConflictException on every version bump. That is why it was reverted, and this test
        // is what stops it being re-landed on the strength of the intent alone.
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var asOf = Instant.FromDateTimeOffset(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        SetMemories.Handler set = NewSet();

        // v1 valid in the past, v2 valid from the future onwards. At `asOf` neither is "current and
        // valid", which is the honest answer; a rewind would instead return v1's claim.
        SetMemories.Request first = Approved(group.Uuid, "Versioned fact", "Old claim");
        first = first with
        {
            Items = [first.Items[0] with { ValidFrom = asOf.Plus(Duration.FromDays(-10)), ValidUntil = asOf.Plus(Duration.FromDays(-5)) }],
        };
        Guid uuid = (await set.Handle(first, Ct)).Items[0].Uuid!.Value;

        SetMemories.Request second = Approved(group.Uuid, "Versioned fact", "New claim");
        second = second with
        {
            Items = [second.Items[0] with
            {
                Uuid = uuid,
                ValidFrom = asOf.Plus(Duration.FromDays(1)),
                ValidUntil = null,
            }],
        };
        await set.Handle(second, Ct);

        QueryMemories.Handler query = NewQuery();

        // CurrentOnly (the default) with AsOf: the current version is judged against the window, and
        // v2 is not yet valid at asOf — so the memory drops out rather than falling back to v1.
        (await query.Handle(Query() with { AsOf = asOf }, Ct))
            .Items.ShouldBeEmpty();

        // Without CurrentOnly the historical version is a candidate in its own right, and it is
        // still the window that decides — v1's window closed before asOf, so it is excluded too.
        (await query.Handle(Query() with { AsOf = asOf, CurrentOnly = false }, Ct))
            .Items.ShouldBeEmpty();

        // A point inside v1's own window still returns nothing under the default CurrentOnly: v1 is
        // no longer current, and the query does not rewind to find it. This is the statement that
        // would fail if the reverted experiment were re-landed.
        (await query.Handle(Query() with { AsOf = asOf.Plus(Duration.FromDays(-7)) }, Ct)).Items.ShouldBeEmpty();

        // Dropping CurrentOnly makes v1 a candidate in its own right, and then its window admits it —
        // so the version is returned because it is valid, not because anything rewound.
        (await query.Handle(Query() with { AsOf = asOf.Plus(Duration.FromDays(-7)), CurrentOnly = false }, Ct))
            .Items.Select(i => i.Statement).ShouldBe(["Old claim"]);

        // And with no AsOf the current version is what comes back, as everywhere else.
        (await query.Handle(Query(), Ct)).Items.Select(i => i.Statement).ShouldBe(["New claim"]);
    }

    [Fact]
    public async Task Limit_caps_the_result_set()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        for (int i = 0; i < 3; i++)
        {
            await set.Handle(Approved(group.Uuid, $"Fact {i}"), Ct);
        }

        (await NewQuery().Handle(Query() with { Limit = 2 }, Ct)).Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_ticket_returns_empty_not_an_error()
    {
        QueryMemories.Response response = await NewQuery().Handle(
            Query() with { TicketProvider = "jira", TicketKey = "NOPE-1" },
            Ct);

        response.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ticket_query_returns_the_owner_group_union_its_references()
    {
        var alpha = new Initiative { Name = "InitiativeAlpha" };
        var beta = new Initiative { Name = "InitiativeBeta" };
        Db.Initiatives.AddRange(alpha, beta);
        await Db.SaveChangesAsync(Ct);

        var owner = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "231", "https://tracker/231")]);
        owner.InitiativeId = alpha.Id;
        var referenceGroup = TestEntities.NewGroup();
        referenceGroup.InitiativeId = beta.Id;
        Db.MemoryGroups.AddRange(owner, referenceGroup);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(owner.Uuid, "Owner fact"), Ct);
        SetMemories.Request reference = Approved(referenceGroup.Uuid, "Referencing fact");
        reference = reference with { TicketReferences = [new TicketReferenceInput("github", "231")] };
        await set.Handle(reference, Ct);

        QueryMemories.Handler query = NewQuery();

        var union = await query.Handle(
            Query() with { TicketProvider = "github", TicketKey = "231" }, Ct);
        union.Items.Select(i => i.Description).ShouldBe(["Owner fact", "Referencing fact"], ignoreOrder: true);

        union.Items.Single(i => i.Description == "Owner fact").TicketMatch.ShouldBe("owner");
        union.Items.Single(i => i.Description == "Referencing fact").TicketMatch.ShouldBe("reference");

        // Every other filter stays conjunctive over the union: an initiative narrows to one branch.
        (await query.Handle(
            Query() with { TicketProvider = "github", TicketKey = "231", InitiativeName = "InitiativeAlpha" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Owner fact"]);
        (await query.Handle(
            Query() with { TicketProvider = "github", TicketKey = "231", InitiativeName = "InitiativeBeta" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Referencing fact"]);
    }

    [Fact]
    public async Task Ticket_query_hides_a_program_scoped_reference_without_explicit_scope()
    {
        var alpha = new Initiative { Name = "InitiativeAlpha" };
        var beta = new Initiative { Name = "InitiativeBeta" };
        Db.Initiatives.AddRange(alpha, beta);
        await Db.SaveChangesAsync(Ct);

        var owner = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "231", "https://tracker/231")]);
        owner.InitiativeId = alpha.Id;
        var referenceProgram = TestEntities.NewGroup(scopeDimension: MemoryGroup.ScopeDimensionValue.Program);
        referenceProgram.InitiativeId = beta.Id;
        Db.MemoryGroups.AddRange(owner, referenceProgram);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(owner.Uuid, "Owner fact"), Ct);
        SetMemories.Request reference = Approved(referenceProgram.Uuid, "Program reference");
        reference = reference with { TicketReferences = [new TicketReferenceInput("github", "231")] };
        await set.Handle(reference, Ct);

        QueryMemories.Handler query = NewQuery();

        // D2: a reference is not in-group context, so programme is hidden unless the scope was asked for.
        (await query.Handle(Query() with { TicketProvider = "github", TicketKey = "231" }, Ct))
            .Items.Select(i => i.Description).ShouldBe(["Owner fact"]);

        // The owner group keeps group context, so an owner-group programme memory is still returned.
        var ownerProgram = TestEntities.NewGroup(scopeDimension: MemoryGroup.ScopeDimensionValue.Program);
        ownerProgram.InitiativeId = alpha.Id;
        Db.MemoryGroups.Add(ownerProgram);
        await Db.SaveChangesAsync(Ct);
        await set.Handle(Approved(ownerProgram.Uuid, "Owner programme fact"), Ct);

        (await query.Handle(Query() with { TicketProvider = "github", TicketKey = "231", ScopeDimension = MemoryGroup.ScopeDimensionValue.Program }, Ct))
            .Items.Select(i => i.Description).ShouldContain("Program reference");
    }

    [Fact]
    public async Task Ticket_query_applies_the_limit_after_the_union()
    {
        var alpha = new Initiative { Name = "InitiativeAlpha" };
        var beta = new Initiative { Name = "InitiativeBeta" };
        Db.Initiatives.AddRange(alpha, beta);
        await Db.SaveChangesAsync(Ct);

        var owner = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "231", "https://tracker/231")]);
        owner.InitiativeId = alpha.Id;
        var referenceGroup = TestEntities.NewGroup();
        referenceGroup.InitiativeId = beta.Id;
        Db.MemoryGroups.AddRange(owner, referenceGroup);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(owner.Uuid, "Owner one"), Ct);
        await set.Handle(Approved(owner.Uuid, "Owner two"), Ct);
        await set.Handle(Approved(referenceGroup.Uuid, "Reference one") with { TicketReferences = [new TicketReferenceInput("github", "231")] }, Ct);
        await set.Handle(Approved(referenceGroup.Uuid, "Reference two") with { TicketReferences = [new TicketReferenceInput("github", "231")] }, Ct);

        var response = await NewQuery().Handle(
            Query() with { TicketProvider = "github", TicketKey = "231", Limit = 3 }, Ct);

        response.Items.Count.ShouldBe(3);
    }

    /// <summary>
    /// A row's ticket list is assembled from both homes of the binding — the group's membership array
    /// and the graph's <c>ABOUT</c> edges — so a card names every ticket the memory is bound to and says
    /// which is which. Without the reference leg the UI would hide a binding the query can find.
    /// </summary>
    [Fact]
    public async Task A_row_carries_the_group_owned_and_the_referenced_tickets_each_marked()
    {
        var owner = TestEntities.NewGroup(tickets:
        [
            TicketDocument.Create("github", "231", "https://tracker/231"),
            TicketDocument.Create("jira", "GPA-234", "https://tracker/GPA-234"),
        ]);
        var target = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "999", "https://tracker/999")]);
        var referencing = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(owner, target, referencing);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(owner.Uuid, "Owner fact"), Ct);
        await set.Handle(Approved(target.Uuid, "Target fact"), Ct);
        await set.Handle(Approved(referencing.Uuid, "Referencing fact") with
        {
            TicketReferences = [new TicketReferenceInput("github", "999")],
        }, Ct);

        QueryMemories.Response response = await NewQuery().Handle(Query(), Ct);

        CheapMemory owned = response.Items.Single(i => i.Description == "Owner fact");
        owned.Tickets
            .Select(t => (t.Provider, t.Key, t.Match))
            .ShouldBe(
                [
                    ("github", "231", TicketMatchValue.Owner),
                    ("jira", "GPA-234", TicketMatchValue.Owner),
                ],
                ignoreOrder: true);

        CheapMemory referenced = response.Items.Single(i => i.Description == "Referencing fact");
        referenced.Tickets
            .Select(t => (t.Provider, t.Key, t.Match))
            .ShouldBe([("github", "999", TicketMatchValue.Reference)]);
    }

    /// <summary>
    /// The membership array is the ownership authority (LADR-08), so a duplicate <c>ABOUT</c> edge on a
    /// ticket the group already owns is one binding, not two — otherwise the card would name it twice.
    /// The write path refuses such a reference, so the edge is created directly to reach the defence.
    /// </summary>
    [Fact]
    public async Task An_owned_ticket_referenced_by_its_own_group_is_one_owner_binding()
    {
        var group = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "231", "https://tracker/231")]);
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memory = (await NewSet().Handle(Approved(group.Uuid, "Doubly bound fact"), Ct)).Items[0];
        await Graph.CreateAboutAsync(memory.Uuid!.Value, "github", "231", "declared outside the write path", Ct);

        CheapMemory row = (await NewQuery().Handle(Query(), Ct)).Items.ShouldHaveSingleItem();

        row.Tickets
            .Select(t => (t.Provider, t.Key, t.Match))
            .ShouldBe([("github", "231", TicketMatchValue.Owner)]);
    }

    /// <summary>
    /// The assembly is anchored on the returned page, so one memory's reference must not surface on a
    /// sibling row of the same query.
    /// </summary>
    [Fact]
    public async Task A_reference_stays_on_its_own_row_within_the_page()
    {
        var target = TestEntities.NewGroup(tickets: [TicketDocument.Create("github", "999", "https://tracker/999")]);
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.AddRange(target, group);
        await Db.SaveChangesAsync(Ct);

        SetMemories.Handler set = NewSet();
        await set.Handle(Approved(target.Uuid, "Target fact"), Ct);
        await set.Handle(Approved(group.Uuid, "Referencing fact") with
        {
            TicketReferences = [new TicketReferenceInput("github", "999")],
        }, Ct);
        await set.Handle(Approved(group.Uuid, "Unrelated fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(Query(), Ct);

        response.Items.Single(i => i.Description == "Unrelated fact").Tickets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inspect_reports_scope_and_match_count_without_rows()
    {
        var product = TestEntities.NewGroup();
        var program = TestEntities.NewGroup(MemoryGroup.ScopeDimensionValue.Program);
        Db.MemoryGroups.AddRange(product, program);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(product.Uuid, "Product fact"), Ct);
        await NewSet().Handle(Approved(program.Uuid, "Program fact"), Ct);

        QueryMemories.Response response = await NewQuery().Handle(Query() with { Inspect = true }, Ct);

        // Inspection ships evidence, never the matched rows.
        response.Items.ShouldBeEmpty();
        QueryMemories.Inspection inspection = response.Inspection.ShouldNotBeNull();
        inspection.IsInspection.ShouldBeTrue();
        inspection.Note.ShouldBeNull();

        // Scope is enforced, not skipped: an open search hides programme and the count reflects it.
        inspection.RequiredScopeDimension.ShouldBeNull();
        inspection.ExcludedScopeDimensions.ShouldContain(MemoryGroup.ScopeDimensionValue.Program);
        inspection.MatchedCount.ShouldBe(1);
        inspection.Limited.ShouldBeFalse();
    }

    [Fact]
    public async Task Inspect_records_no_recall_feedback()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        await NewSet().Handle(Approved(group.Uuid, "Fact"), Ct);

        var feedback = new TrackingRecallFeedback();
        QueryMemories.Response response = await NewQuery(feedback).Handle(Query() with { Inspect = true }, Ct);

        response.Inspection.ShouldNotBeNull();
        feedback.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inspect_reports_the_tsquery_lexemes_for_free_text()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(group.Uuid, "The storage engine", "We chose PostgreSQL"), Ct);

        QueryMemories.Inspection inspection = (await NewQuery().Handle(
            Query() with { Inspect = true, Query = "storage" }, Ct)).Inspection.ShouldNotBeNull();

        // The decomposed predicate the FTS index compares against: the stemmed lexeme, not the raw text.
        inspection.FreeText.ShouldBe("storage");
        inspection.TsQuery.ShouldNotBeNull().ShouldContain("storag");
        inspection.MatchedCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Inspect_unknown_ticket_returns_miss_note_without_rows_or_feedback()
    {
        var feedback = new TrackingRecallFeedback();
        QueryMemories.Response response = await NewQuery(feedback).Handle(
            Query() with { Inspect = true, TicketProvider = "jira", TicketKey = "NOPE-1" }, Ct);

        response.Items.ShouldBeEmpty();
        QueryMemories.Inspection inspection = response.Inspection.ShouldNotBeNull();
        inspection.Note.ShouldBe("ticket-miss");
        inspection.MatchedCount.ShouldBe(0);
        feedback.Records.ShouldBeEmpty();
    }

    /// <summary>
    /// A malformed membership element must not cost the practitioner the page: the read is unnested in
    /// SQL, so the half-ticket is dropped and the page still renders.
    /// </summary>
    [Fact]
    public async Task A_membership_element_without_a_provider_or_key_is_left_out()
    {
        var group = TestEntities.NewGroup();
        group.Tickets.Add(new TicketDocument { Key = "no-provider", Url = "https://tracker/no-provider" });
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(group.Uuid, "Half bound fact"), Ct);

        CheapMemory row = (await NewQuery().Handle(Query(), Ct)).Items.ShouldHaveSingleItem();
        row.Tickets.ShouldBeEmpty();
    }

    private SetMemories.Handler NewSet() =>
        new(AppDb, Graph, Blob, ErrorMapper, ProductGraph, Loggers.CreateLogger<SetMemories.Handler>());

    private QueryMemories.Handler NewQuery() =>
        new(AppDb, Search, new NoopRecallFeedback(), ProductGraph, Graph, Loggers.CreateLogger<QueryMemories.Handler>());

    private QueryMemories.Handler NewQuery(IRecallFeedback feedback) =>
        new(AppDb, Search, feedback, ProductGraph, Graph, Loggers.CreateLogger<QueryMemories.Handler>());

    private sealed class NoopRecallFeedback : IRecallFeedback
    {
        public void Record(RecallFeedbackRecord[] records)
        {
        }
    }

    private sealed class TrackingRecallFeedback : IRecallFeedback
    {
        public List<RecallFeedbackRecord> Records { get; } = [];

        public void Record(RecallFeedbackRecord[] records) => Records.AddRange(records);
    }

    private static QueryMemories.Request Query() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    private static SetMemories.Request Approved(Guid group, string description, string statement = "Claim") =>
        Write(group, description, statement, MemoryVersion.MemoryVersionStatus.Approved);

    private static SetMemories.Request Proposed(Guid group, string description) =>
        Write(group, description, "Claim", MemoryVersion.MemoryVersionStatus.Proposed);

    private static SetMemories.Request Write(Guid group, string description, string statement, string status) =>
        new(
            group,
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    description,
                    statement,
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    status,
                    80,
                    null,
                    null,
                    SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(-1)),
                    null,
                    null,
                    null)
            ],
            null,
            null);
}
