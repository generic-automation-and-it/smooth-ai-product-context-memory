using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 coverage of the shipped recall-feedback persistence (HLD-004 LADR-02 placement B): the
/// append-only write, the bounded-shape CHECK, no append-only-trigger coupling, and the three
/// NFR-03 queries (never-recalled with recency exclusion, miss rate, reset).
/// </summary>
public sealed class RecallFeedbackPersistenceTests(AspireFixture aspire) : PersistenceTestBase(aspire)
{
    private readonly ILoggerFactory _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None));

    private NpgsqlRecallFeedback Writer => new(DataSource, _loggers.CreateLogger<NpgsqlRecallFeedback>());

    private NpgsqlRecallFeedbackQuery Reader => new(DataSource, _loggers.CreateLogger<NpgsqlRecallFeedbackQuery>());

    [Fact]
    public async Task Write_persists_hit_records_sharing_a_retrieval_id()
    {
        Guid retrievalId = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        Writer.Record(
        [
            new RecallFeedbackRecord(retrievalId, first, RetrievalShape.Unfiltered, DateTimeOffset.UtcNow),
            new RecallFeedbackRecord(retrievalId, second, RetrievalShape.Unfiltered, DateTimeOffset.UtcNow),
        ]);

        List<(Guid RetrievalId, Guid? MemoryUuid, string Shape)> rows = await ReadRowsAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.RetrievalId == retrievalId);
        rows.Select(r => r.MemoryUuid).ShouldBe([first, second]);
    }

    [Fact]
    public async Task Write_persists_miss_record_with_null_memory_uuid()
    {
        Writer.Record([new RecallFeedbackRecord(Guid.NewGuid(), null, RetrievalShape.FreeText, DateTimeOffset.UtcNow)]);

        List<(Guid RetrievalId, Guid? MemoryUuid, string Shape)> rows = await ReadRowsAsync();
        rows.Single().MemoryUuid.ShouldBeNull();
    }

    [Fact]
    public async Task Shape_check_rejects_out_of_set_value()
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.recall_feedback (retrieval_id, memory_uuid, shape, occurred_on)
            VALUES (@r, @m, 'free text leaking content', @o)
            """,
            connection);
        command.Parameters.AddWithValue("r", Guid.NewGuid());
        command.Parameters.AddWithValue("m", DBNull.Value);
        command.Parameters.AddWithValue("o", DateTimeOffset.UtcNow);

        NpgsqlException ex = await Should.ThrowAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync(Ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Feedback_table_has_no_append_only_trigger()
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_trigger WHERE tgrelid = 'public.recall_feedback'::regclass",
            connection);

        long count = (long)(await command.ExecuteScalarAsync(Ct) ?? 0L);
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Never_recalled_excludes_recently_captured_and_recalled()
    {
        (_, Memory neverRecalled, Memory recalled, _) = await SeedMemoriesAsync();
        Writer.Record([new RecallFeedbackRecord(Guid.NewGuid(), recalled.Uuid, RetrievalShape.Unfiltered, DateTimeOffset.UtcNow)]);

        IReadOnlyList<NeverRecalledRow> result =
            await Reader.NeverRecalledAsync(
                new NeverRecalledRequest(DateTimeOffset.UtcNow), Ct);

        result.Select(r => r.MemoryUuid).ShouldBe([neverRecalled.Uuid]);
    }

    [Fact]
    public async Task Miss_rate_over_window_is_derivable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Writer.Record([new RecallFeedbackRecord(Guid.NewGuid(), null, RetrievalShape.FreeText, now)]);
        Writer.Record([new RecallFeedbackRecord(Guid.NewGuid(), Guid.NewGuid(), RetrievalShape.Unfiltered, now)]);

        MissRateResult result =
            await Reader.MissRateAsync(new MissRateRequest(now.AddHours(-1), now.AddHours(1)), Ct);

        result.Retrievals.ShouldBe(2);
        result.Misses.ShouldBe(1);
        result.MissRate.ShouldBe(0.5);
    }

    [Fact]
    public async Task Reset_clears_feedback()
    {
        Writer.Record([new RecallFeedbackRecord(Guid.NewGuid(), null, RetrievalShape.FreeText, DateTimeOffset.UtcNow)]);

        int deleted = await Reader.ResetAsync(Ct);

        deleted.ShouldBe(1);
        (await ReadRowsAsync()).ShouldBeEmpty();
    }

    private async Task<(MemoryGroup Group, Memory NeverRecalled, Memory Recalled, Memory Recent)> SeedMemoriesAsync()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        Memory never = TestEntities.NewMemory(group.Id, "Never", "Never recalled fact");
        Memory recalled = TestEntities.NewMemory(group.Id, "Recalled", "Recalled fact");
        Memory recent = TestEntities.NewMemory(group.Id, "Recent", "Recently captured fact");
        Db.Memories.AddRange(never, recalled, recent);
        await Db.SaveChangesAsync(Ct);

        await AddVersionAsync(never.Id, DateTimeOffset.UtcNow.AddDays(-30));
        await AddVersionAsync(recalled.Id, DateTimeOffset.UtcNow.AddDays(-30));
        await AddVersionAsync(recent.Id, DateTimeOffset.UtcNow);

        return (group, never, recalled, recent);
    }

    private async Task AddVersionAsync(long memoryId, DateTimeOffset createdOn)
    {
        MemoryVersion version = TestEntities.NewVersion(memoryId, 1, "Claim");
        version.CreatedOn = createdOn;
        Db.MemoryVersions.Add(version);
        await Db.SaveChangesAsync(Ct);
    }

    private async Task<List<(Guid RetrievalId, Guid? MemoryUuid, string Shape)>> ReadRowsAsync()
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT retrieval_id, memory_uuid, shape FROM public.recall_feedback ORDER BY retrieval_id, memory_uuid",
            connection);

        var rows = new List<(Guid, Guid?, string)>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2)));
        }

        return rows;
    }
}
