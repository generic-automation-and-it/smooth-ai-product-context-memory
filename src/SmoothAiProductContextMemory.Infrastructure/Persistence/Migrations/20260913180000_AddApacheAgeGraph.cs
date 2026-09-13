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
        // Each step is guarded by an ag_catalog existence check: with suppressTransaction a
        // mid-batch failure leaves partial state and the migration unrecorded, and a naive
        // re-run would fail on create_graph. The guards make re-running safe.
        migrationBuilder.Sql(
            """
            CREATE EXTENSION IF NOT EXISTS age;
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_up$
            DECLARE
                graph_oid oid;
                edge_label text;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'memory_graph') THEN
                    PERFORM ag_catalog.create_graph('memory_graph');
                END IF;

                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';

                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Memory') THEN
                    PERFORM ag_catalog.create_vlabel('memory_graph', 'Memory');
                END IF;

                FOREACH edge_label IN ARRAY ARRAY['depends_on', 'relates_to', 'contradicts', 'supersedes', 'implements'] LOOP
                    IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = edge_label) THEN
                        -- AGE's create_elabel only resolves for untyped literals; EXECUTE keeps them unknown.
                        EXECUTE format('SELECT ag_catalog.create_elabel(%L, %L)', 'memory_graph', edge_label);
                    END IF;
                END LOOP;
            END
            $age_up$;
            """,
            suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Symmetric guard: drop_graph fails hard on an absent graph, which would strand a
        // partially applied Up (extension present, graph missing) in an unrevertable state.
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_down$
            BEGIN
                -- Nested check: ag_catalog only exists while the extension does; plpgsql
                -- resolves the inner query lazily, so it is safe when the extension is absent.
                IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'age') THEN
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'memory_graph') THEN
                        PERFORM ag_catalog.drop_graph('memory_graph', true);
                    END IF;
                END IF;
            END
            $age_down$;
            DROP EXTENSION IF EXISTS age;
            """,
            suppressTransaction: true);
    }
}
