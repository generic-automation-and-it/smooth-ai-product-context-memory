using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <summary>
/// Swaps the two full-text GIN indexes from the <c>simple</c> to the <c>english</c> text-search
/// configuration, closing the HLD-001 deferred stemming follow-up. Measured on the committed recall
/// fixture (<c>RecallTuningEvidenceTests</c>): recall 0.44 → 0.78 on inflection queries at 0.88
/// precision; evidence in HLD-001 NFR-02 recall-tuning measurements. The query side
/// (<c>NpgsqlMemorySearch</c>) changes configuration in the same commit — index expression and query
/// expression must always name the same configuration or the index silently stops serving.
/// </summary>
[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20260916090000_StemFullTextIndexes")]
[ExcludeFromCodeCoverage]
public partial class StemFullTextIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_name_description_fts;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_version_statement_summary_fts;");

        migrationBuilder.Sql(
            """
            CREATE INDEX ix_memory_name_description_fts
            ON memory USING gin (to_tsvector('english', name || ' ' || description));
            """);

        migrationBuilder.Sql(
            """
            CREATE INDEX ix_memory_version_statement_summary_fts
            ON memory_version USING gin (to_tsvector('english', statement || ' ' || content_summary));
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_name_description_fts;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_version_statement_summary_fts;");

        migrationBuilder.Sql(
            """
            CREATE INDEX ix_memory_name_description_fts
            ON memory USING gin (to_tsvector('simple', name || ' ' || description));
            """);

        migrationBuilder.Sql(
            """
            CREATE INDEX ix_memory_version_statement_summary_fts
            ON memory_version USING gin (to_tsvector('simple', statement || ' ' || content_summary));
            """);
    }
}
