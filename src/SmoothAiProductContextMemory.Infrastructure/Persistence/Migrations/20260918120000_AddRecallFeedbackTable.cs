using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <summary>
/// Creates the append-only recall-feedback table (HLD-004 LADR-02 placement B). Deliberately outside
/// the six EF entities: not exposed as a DbSet, invisible to the model snapshot, no trigger, and
/// excluded from backup and restore. The <c>shape</c> CHECK constrains the retrieval-shape category
/// so it cannot become free text (NFR-01).
/// </summary>
[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20260918120000_AddRecallFeedbackTable")]
[ExcludeFromCodeCoverage]
public partial class AddRecallFeedbackTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS public.recall_feedback (
                retrieval_id uuid NOT NULL,
                memory_uuid  uuid NULL,
                shape        text NOT NULL,
                occurred_on  timestamptz NOT NULL,
                CONSTRAINT ck_recall_feedback_shape CHECK (shape IN (
                    'free_text', 'facet_only', 'ticket_scoped', 'group_scoped', 'unfiltered'))
            );

            CREATE INDEX IF NOT EXISTS ix_recall_feedback_memory_uuid
                ON public.recall_feedback (memory_uuid);
            CREATE INDEX IF NOT EXISTS ix_recall_feedback_occurred_on
                ON public.recall_feedback (occurred_on DESC);
            CREATE INDEX IF NOT EXISTS ix_recall_feedback_retrieval
                ON public.recall_feedback (retrieval_id);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS public.recall_feedback;");
    }
}
