using System.Text;
using Npgsql;
using SmoothAiProductContextMemory.Application.Abstractions;
using SmoothAiProductContextMemory.Domain.Entities;
using SmoothAiProductContextMemory.Infrastructure.Persistence;

namespace SmoothAiProductContextMemory.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 — pins the shipped <c>english</c> full-text behaviour of <see cref="NpgsqlMemorySearch"/> against
/// the real provider: inflected queries now recall their subject (the measured reason the
/// configuration changed; see HLD-001 NFR-02 recall-tuning measurements), related-but-distinct
/// subjects stay out, the one known stemming collision is pinned as a documented candidate-widening
/// trade, and free text composes with the lifecycle filters rather than bypassing them. The plan test
/// proves the query-side expression still matches the index expression — the pair silently decouples
/// if only one side changes configuration.
/// </summary>
public sealed class FreeTextRecallTests : PersistenceTestBase
{
    public FreeTextRecallTests(AspireFixture aspire) : base(aspire) { }

    private NpgsqlMemorySearch Search => new(Db);

    [Fact]
    public async Task Inflected_query_recalls_the_stored_subject()
    {
        await SeedCorpusAsync();

        var rows = await Search.SearchAsync(new MemorySearchCriteria { FreeText = "migration" }, Ct);

        rows.Select(r => r.Name).ShouldContain("Database migrations policy");
    }

    [Fact]
    public async Task Related_but_distinct_subject_stays_out()
    {
        await SeedCorpusAsync();

        var rows = await Search.SearchAsync(new MemorySearchCriteria { FreeText = "session handling" }, Ct);

        rows.ShouldBeEmpty();
    }

    /// <summary>
    /// The measured trade of the english configuration: "authorization" and "authors" share the stem
    /// <c>author</c>, so this query returns one extra candidate. That is acceptable — results are
    /// candidates for the skill's semantic judgement, bounded by the limit — but it must stay a
    /// deliberate, pinned behaviour rather than drift. If this fails after a text-search change,
    /// re-run the recall-tuning evidence before adjusting the assertion.
    /// </summary>
    [Fact]
    public async Task Known_stem_collision_is_a_pinned_candidate_widening()
    {
        await SeedCorpusAsync();

        var rows = await Search.SearchAsync(new MemorySearchCriteria { FreeText = "authorization registry" }, Ct);

        rows.Select(r => r.Name).ShouldBe(["Book authors registry"]);
    }

    [Fact]
    public async Task Free_text_composes_with_lifecycle_filters_instead_of_bypassing_them()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        var memory = TestEntities.NewMemory(group.Id, "Database migrations policy", "How schema migrations are reviewed");
        Db.Memories.Add(memory);
        await Db.SaveChangesAsync(Ct);
        Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, "Old migration rule", isCurrent: false));
        var proposed = TestEntities.NewVersion(memory.Id, 2, "Proposed migration rule");
        proposed.Status = MemoryVersion.MemoryVersionStatus.Proposed;
        Db.MemoryVersions.Add(proposed);
        await Db.SaveChangesAsync(Ct);

        var rows = await Search.SearchAsync(new MemorySearchCriteria { FreeText = "migration" }, Ct);

        // Current-only excludes v1; the default proposed exclusion removes v2 — free text must not
        // resurrect either.
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Free_text_predicate_is_served_by_the_fts_gin_index()
    {
        await SeedCorpusAsync();

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(Ct);
        await using (var setCmd = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conn, tx))
        {
            await setCmd.ExecuteNonQueryAsync(Ct);
        }

        // The single-table shape the memory-side OR branch emits. If the query-side configuration and
        // the index configuration diverge, this stops naming the index.
        await using var cmd = new NpgsqlCommand(
            """
            EXPLAIN (FORMAT TEXT) SELECT id FROM memory
            WHERE to_tsvector('english', name || ' ' || description) @@ plainto_tsquery('english', $1)
            """,
            conn,
            tx);
        cmd.Parameters.AddWithValue("migration");
        var plan = new StringBuilder();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        await tx.RollbackAsync(Ct);

        plan.ToString().ShouldContain("ix_memory_name_description_fts");
        plan.ToString().ShouldNotContain("Seq Scan");
    }

    private async Task SeedCorpusAsync()
    {
        var group = TestEntities.NewGroup();
        Db.MemoryGroups.Add(group);
        await Db.SaveChangesAsync(Ct);

        (string Name, string Description, string Statement)[] corpus =
        [
            ("Database migrations policy", "How schema migrations are reviewed", "All schema migrations require a reversible down step"),
            ("Section heading style", "Formatting of section headings in documents", "Section headings use sentence case"),
            ("Book authors registry", "Tracking cited authors and citations", "Every cited author appears once in the registry"),
            ("Authentication flow", "Login token issuance", "Login issues a short lived token"),
        ];

        foreach ((string name, string description, string statement) in corpus)
        {
            var memory = TestEntities.NewMemory(group.Id, name, description);
            Db.Memories.Add(memory);
            await Db.SaveChangesAsync(Ct);
            Db.MemoryVersions.Add(TestEntities.NewVersion(memory.Id, 1, statement));
        }

        await Db.SaveChangesAsync(Ct);
    }
}
