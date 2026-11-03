using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20261003160000_AddRecallAttribution")]
[ExcludeFromCodeCoverage]
public sealed class AddRecallAttribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.recall_feedback
            ADD COLUMN purpose varchar(32) NOT NULL DEFAULT 'direct_retrieval',
            ADD COLUMN caller_request_id uuid NULL,
            ADD CONSTRAINT ck_recall_feedback_purpose CHECK (purpose IN ('direct_retrieval','service_retrieval','capture_comparison'));
        CREATE INDEX ix_recall_feedback_purpose_occurred_on ON public.recall_feedback (purpose, occurred_on);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX public.ix_recall_feedback_purpose_occurred_on;
        ALTER TABLE public.recall_feedback DROP CONSTRAINT ck_recall_feedback_purpose,
            DROP COLUMN purpose, DROP COLUMN caller_request_id;
        """);
}
