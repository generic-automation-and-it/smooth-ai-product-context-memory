using Microsoft.Extensions.Logging;
using Shouldly;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Application.Common.Models;
using SmoothAiProductContextMemory.Application.Features.Memories;
using SmoothAiProductContextMemory.Domain.Entities;

namespace SmoothAiProductContextMemory.Application.ComponentTest.Features;

/// <summary>
/// Verifies the retrieval handler emits recall-feedback outcomes (HLD-004 LADR-01) off the critical
/// path: hits write one record per returned memory sharing a retrieval id, misses write one record
/// with a null memory uuid, a broken feedback path never fails a retrieval, and the retrieval result
/// is byte-identical with feedback on and off.
/// </summary>
public sealed class RecallFeedbackHandlerTests(AspireFixture aspire) : HandlerTestBase(aspire)
{
    [Fact]
    public async Task Hit_emits_one_record_per_memory_sharing_a_retrieval_id()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        await NewSet().Handle(Approved(group.Uuid, "First fact"), Ct);
        await NewSet().Handle(Approved(group.Uuid, "Second fact"), Ct);

        var spy = new SpyRecallFeedback();
        QueryMemories.Response response = await NewQuery(spy).Handle(Query(), Ct);

        response.Items.Count.ShouldBe(2);
        spy.Records.Count.ShouldBe(2);
        spy.Records.Select(r => r.RetrievalId).Distinct().Single().ShouldNotBe(Guid.Empty);
        spy.Records.Select(r => r.MemoryUuid).ShouldBe(response.Items.Select(i => (Guid?)i.Uuid));
        spy.Records.ShouldAllBe(r => r.Shape == RetrievalShape.Unfiltered);
    }

    [Fact]
    public async Task Miss_emits_one_record_with_null_memory_uuid()
    {
        var spy = new SpyRecallFeedback();
        QueryMemories.Response response = await NewQuery(spy).Handle(
            Query() with { TicketProvider = "jira", TicketKey = "NOPE-1" },
            Ct);

        response.Items.ShouldBeEmpty();
        var record = spy.Records.Single();
        record.MemoryUuid.ShouldBeNull();
        record.Shape.ShouldBe(RetrievalShape.TicketScoped);
    }

    [Fact]
    public async Task Broken_feedback_write_does_not_fail_the_retrieval()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        await NewSet().Handle(Approved(group.Uuid, "First fact"), Ct);

        var throwing = new ThrowingRecallFeedback();
        QueryMemories.Response response = await NewQuery(throwing).Handle(Query(), Ct);

        response.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Result_is_byte_identical_with_feedback_on_and_off()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);
        await NewSet().Handle(Approved(group.Uuid, "First fact"), Ct);

        QueryMemories.Response withFeedback = await NewQuery(new SpyRecallFeedback()).Handle(Query(), Ct);
        QueryMemories.Response withoutFeedback = await NewQuery(new NoopRecallFeedback()).Handle(Query(), Ct);

        // CheapMemory's record equality uses reference equality for its list fields, so compare a
        // deep projection (scalar fields + list contents) to prove the result is byte-identical.
        withFeedback.Items.Select(Key).ShouldBe(withoutFeedback.Items.Select(Key));

        static (Guid Uuid, string Description, string Statement, int Version, string Kind, short Confidence,
            string ScopeDimension, string Facets, string Tags) Key(CheapMemory x) =>
            (x.Uuid, x.Description, x.Statement, x.Version, x.Kind, x.Confidence, x.ScopeDimension,
                string.Join(',', x.Facets), string.Join(',', x.Tags));
    }

    private QueryMemories.Handler NewQuery(IRecallFeedback feedback) =>
        new(AppDb, Search, feedback, Loggers.CreateLogger<QueryMemories.Handler>());

    private SetMemories.Handler NewSet() =>
        new(AppDb, Graph, Blob, ErrorMapper, Loggers.CreateLogger<SetMemories.Handler>());

    private static QueryMemories.Request Query() =>
        new(null, null, null, null, null, null, null, null, null, null, null);

    private static SetMemories.Request Approved(Guid group, string description) =>
        new(
            group,
            [
                new SetMemories.MemoryWrite(
                    null,
                    "Name",
                    description,
                    "Claim",
                    "Summary",
                    MemoryVersion.KindValue.Decision,
                    null,
                    null,
                    MemoryVersion.MemoryVersionStatus.Approved,
                    80,
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    null,
                    null,
                    null),
            ],
            null,
            null);

    private sealed class SpyRecallFeedback : IRecallFeedback
    {
        public List<RecallFeedbackRecord> Records { get; } = [];

        public void Record(RecallFeedbackRecord[] records) => Records.AddRange(records);
    }

    private sealed class ThrowingRecallFeedback : IRecallFeedback
    {
        public void Record(RecallFeedbackRecord[] records) => throw new InvalidOperationException("broken");
    }

    private sealed class NoopRecallFeedback : IRecallFeedback
    {
        public void Record(RecallFeedbackRecord[] records)
        {
        }
    }
}
