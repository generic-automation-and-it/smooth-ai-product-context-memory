using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[ExcludeFromCodeCoverage]
public partial class AddApacheAgeGraph : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // AGE catalog writes (CREATE EXTENSION, create_graph, create_*label) are ordinary
        // writes: they are invisible to other sessions until committed. EF wraps each
        // migration in a transaction by default, so these statements must run outside it.
        // LOAD is per-session and must precede the catalog functions.
        //
        // Install ordering: the extension is created here, as the migrating role, before
        // any other role is granted schema-creation rights. Otherwise ag_catalog can be
        // pre-created under the wrong owner and installation refuses.
        migrationBuilder.Sql(
            """
            CREATE EXTENSION IF NOT EXISTS age;
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            SELECT create_graph('memory_graph');
            SELECT create_vlabel('memory_graph', 'Memory');
            SELECT create_elabel('memory_graph', 'depends_on');
            SELECT create_elabel('memory_graph', 'relates_to');
            SELECT create_elabel('memory_graph', 'contradicts');
            SELECT create_elabel('memory_graph', 'supersedes');
            SELECT create_elabel('memory_graph', 'implements');
            """,
            suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            SELECT drop_graph('memory_graph', true);
            DROP EXTENSION IF EXISTS age;
            """,
            suppressTransaction: true);
    }
}
