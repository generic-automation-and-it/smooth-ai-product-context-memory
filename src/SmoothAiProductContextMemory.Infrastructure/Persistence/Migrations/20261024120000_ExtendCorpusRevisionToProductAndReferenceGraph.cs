using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20261024120000_ExtendCorpusRevisionToProductAndReferenceGraph")]
[ExcludeFromCodeCoverage]
public sealed class ExtendCorpusRevisionToProductAndReferenceGraph : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (string label in Labels)
        {
            migrationBuilder.Sql($"CREATE TRIGGER advance_corpus_revision BEFORE INSERT OR UPDATE OR DELETE ON memory_graph.\"{label}\" FOR EACH STATEMENT EXECUTE FUNCTION public.advance_corpus_revision();");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (string label in Labels)
        {
            migrationBuilder.Sql($"DROP TRIGGER advance_corpus_revision ON memory_graph.\"{label}\";");
        }
    }

    private static readonly string[] Labels =
        ["ABOUT", "Product", "Initiative", "Repo", "HAS_INITIATIVE", "HAS_REPO"];
}
