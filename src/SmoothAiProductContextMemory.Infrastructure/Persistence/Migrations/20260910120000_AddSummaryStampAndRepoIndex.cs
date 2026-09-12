using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[ExcludeFromCodeCoverage]
public partial class AddSummaryStampAndRepoIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "summary_stamp",
            table: "memory_version",
            type: "jsonb",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_memory_group_repo",
            table: "memory_group",
            column: "repo");

        // Recreate the append-only guard with summary_stamp in the equality list.
        // A new column omitted from this list is silently mutable on a legal version-bump UPDATE.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.append_only_guard() RETURNS trigger AS $$
            BEGIN
                IF TG_TABLE_NAME = 'memory_version' AND TG_OP = 'UPDATE' THEN
                    IF NEW.is_current IS DISTINCT FROM OLD.is_current
                       AND NEW.memory_id = OLD.memory_id
                       AND NEW.version = OLD.version
                       AND NEW.statement = OLD.statement
                       AND NEW.content_summary = OLD.content_summary
                       AND NEW.blob_address IS NOT DISTINCT FROM OLD.blob_address
                       AND NEW.kind = OLD.kind
                       AND NEW.confidence = OLD.confidence
                       AND NEW.status = OLD.status
                       AND NEW.sources = OLD.sources
                       AND NEW.valid_from = OLD.valid_from
                       AND NEW.valid_until IS NOT DISTINCT FROM OLD.valid_until
                       AND NEW.created_on = OLD.created_on
                       AND NEW.summary_stamp IS NOT DISTINCT FROM OLD.summary_stamp THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Append-only history: UPDATE on % is not permitted', TG_TABLE_NAME;
                END IF;
                IF TG_OP = 'DELETE' AND current_setting('app.allow_history_delete', true) = 'true' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'Append-only history: % on % is not permitted', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.append_only_guard() RETURNS trigger AS $$
            BEGIN
                IF TG_TABLE_NAME = 'memory_version' AND TG_OP = 'UPDATE' THEN
                    IF NEW.is_current IS DISTINCT FROM OLD.is_current
                       AND NEW.memory_id = OLD.memory_id
                       AND NEW.version = OLD.version
                       AND NEW.statement = OLD.statement
                       AND NEW.content_summary = OLD.content_summary
                       AND NEW.blob_address IS NOT DISTINCT FROM OLD.blob_address
                       AND NEW.kind = OLD.kind
                       AND NEW.confidence = OLD.confidence
                       AND NEW.status = OLD.status
                       AND NEW.sources = OLD.sources
                       AND NEW.valid_from = OLD.valid_from
                       AND NEW.valid_until IS NOT DISTINCT FROM OLD.valid_until
                       AND NEW.created_on = OLD.created_on THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Append-only history: UPDATE on % is not permitted', TG_TABLE_NAME;
                END IF;
                IF TG_OP = 'DELETE' AND current_setting('app.allow_history_delete', true) = 'true' THEN
                    RETURN OLD;
                END IF;
                RAISE EXCEPTION 'Append-only history: % on % is not permitted', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;
            """);

        migrationBuilder.DropIndex(
            name: "IX_memory_group_repo",
            table: "memory_group");

        migrationBuilder.DropColumn(
            name: "summary_stamp",
            table: "memory_version");
    }
}
