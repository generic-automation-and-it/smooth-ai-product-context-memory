using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20261003120000_AddCorpusCommitBoundary")]
[ExcludeFromCodeCoverage]
public sealed class AddCorpusCommitBoundary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE public.corpus_state (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                epoch uuid NOT NULL,
                revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0)
            );
            INSERT INTO public.corpus_state (epoch) VALUES (gen_random_uuid());
            CREATE TABLE public.operation_receipt (
                operation_key varchar(200) PRIMARY KEY,
                payload_hash varchar(64) NOT NULL CHECK (payload_hash ~ '^[a-f0-9]{64}$'),
                operation_type varchar(32) NOT NULL,
                result jsonb NOT NULL,
                committed_at timestamptz NOT NULL
            );
            CREATE FUNCTION public.advance_corpus_revision() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                UPDATE public.corpus_state SET revision = revision + 1 WHERE singleton;
                RETURN NULL;
            END;
            $$;
            """);
        foreach (string table in Tables)
        {
            migrationBuilder.Sql($"CREATE TRIGGER advance_corpus_revision BEFORE INSERT OR UPDATE OR DELETE ON {table} FOR EACH STATEMENT EXECUTE FUNCTION public.advance_corpus_revision();");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (string table in Tables)
        {
            migrationBuilder.Sql($"DROP TRIGGER advance_corpus_revision ON {table};");
        }

        migrationBuilder.Sql("DROP FUNCTION public.advance_corpus_revision(); DROP TABLE public.operation_receipt; DROP TABLE public.corpus_state;");
    }

    private static readonly string[] Tables =
    [
        "public.initiative", "public.label", "public.memory_group", "public.group_description", "public.memory", "public.memory_version",
        "memory_graph.\"Memory\"", "memory_graph.\"LINKS\"", "memory_graph.\"Ticket\"", "memory_graph.\"TICKET_PARENT\"",
    ];
}
