using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the product-name graph (HLD-003 LADR-10): <c>Product</c>, <c>Initiative</c> and <c>Repo</c>
/// vertex labels and the <c>HAS_INITIATIVE</c> / <c>HAS_REPO</c> edge labels, plus name indexes.
/// Products are graph-only; nothing here touches the relational schema or the <c>product</c> scope.
/// </summary>
[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20261015160000_AddProductGraph")]
[ExcludeFromCodeCoverage]
public sealed class AddProductGraph : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // AGE labels must commit before a later batch can address them — same rule as AddTicketGraph.
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $labels$
            DECLARE graph_oid oid;
            BEGIN
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                IF graph_oid IS NULL THEN
                    RAISE EXCEPTION 'memory_graph is missing';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Product') THEN
                    PERFORM ag_catalog.create_vlabel('memory_graph', 'Product');
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Initiative') THEN
                    PERFORM ag_catalog.create_vlabel('memory_graph', 'Initiative');
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Repo') THEN
                    PERFORM ag_catalog.create_vlabel('memory_graph', 'Repo');
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'HAS_INITIATIVE') THEN
                    PERFORM ag_catalog.create_elabel('memory_graph', 'HAS_INITIATIVE');
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'HAS_REPO') THEN
                    PERFORM ag_catalog.create_elabel('memory_graph', 'HAS_REPO');
                END IF;
            END
            $labels$;
            """, suppressTransaction: true);

        migrationBuilder.Sql(
            """
            -- Name is the identity of each registry/entity vertex, so lookups and MERGEs on it are
            -- served by a hash index on the extracted property, not the unindexed property containment
            -- scan (the LADR-06 property-predicate rule, mirrored here for Product/Initiative/Repo).
            CREATE INDEX IF NOT EXISTS ix_product_vertex_name ON memory_graph."Product"
                USING hash (
                    ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, '"name"'::ag_catalog.agtype]));
            CREATE INDEX IF NOT EXISTS ix_initiative_vertex_name ON memory_graph."Initiative"
                USING hash (
                    ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, '"name"'::ag_catalog.agtype]));
            CREATE INDEX IF NOT EXISTS ix_repo_vertex_name ON memory_graph."Repo"
                USING hash (
                    ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, '"name"'::ag_catalog.agtype]));
            CREATE INDEX IF NOT EXISTS ix_product_vertex_properties ON memory_graph."Product" USING gin (properties);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $down$
            DECLARE graph_oid oid;
            BEGIN
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                IF graph_oid IS NOT NULL THEN
                    DROP INDEX IF EXISTS memory_graph.ix_product_vertex_name;
                    DROP INDEX IF EXISTS memory_graph.ix_initiative_vertex_name;
                    DROP INDEX IF EXISTS memory_graph.ix_repo_vertex_name;
                    DROP INDEX IF EXISTS memory_graph.ix_product_vertex_properties;

                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'HAS_INITIATIVE') THEN
                        EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $p$MATCH ()-[e:HAS_INITIATIVE]->() DELETE e RETURN 1$p$) AS (v agtype)';
                        PERFORM ag_catalog.drop_label('memory_graph', 'HAS_INITIATIVE');
                    END IF;
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'HAS_REPO') THEN
                        EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $p$MATCH ()-[e:HAS_REPO]->() DELETE e RETURN 1$p$) AS (v agtype)';
                        PERFORM ag_catalog.drop_label('memory_graph', 'HAS_REPO');
                    END IF;
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Product') THEN
                        EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $p$MATCH (p:Product) DETACH DELETE p RETURN 1$p$) AS (v agtype)';
                        PERFORM ag_catalog.drop_label('memory_graph', 'Product');
                    END IF;
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Initiative') THEN
                        EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $p$MATCH (i:Initiative) DETACH DELETE i RETURN 1$p$) AS (v agtype)';
                        PERFORM ag_catalog.drop_label('memory_graph', 'Initiative');
                    END IF;
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Repo') THEN
                        EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $p$MATCH (r:Repo) DETACH DELETE r RETURN 1$p$) AS (v agtype)';
                        PERFORM ag_catalog.drop_label('memory_graph', 'Repo');
                    END IF;
                END IF;
            END
            $down$;
            """, suppressTransaction: true);
    }
}
