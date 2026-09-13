using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[ExcludeFromCodeCoverage]
public partial class CutoverMemoryLinksToAge : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Catalog write: create_elabel is invisible to other sessions until committed.
        // suppressTransaction so the copy below (separate batch) can see LINKS.
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_elabel$
            DECLARE
                graph_oid oid;
            BEGIN
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                IF graph_oid IS NULL THEN
                    RAISE EXCEPTION 'memory_graph is missing; AGE foundation migration must run first';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'LINKS') THEN
                    EXECUTE format('SELECT ag_catalog.create_elabel(%L, %L)', 'memory_graph', 'LINKS');
                END IF;
            END
            $age_elabel$;
            """,
            suppressTransaction: true);

        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_copy$
            DECLARE
                rec record;
            BEGIN
                FOR rec IN
                    SELECT s.uuid AS source_uuid,
                           t.uuid AS target_uuid,
                           l.relation,
                           l.reason
                    FROM memory_link l
                    JOIN memory s ON s.id = l.source_memory_id
                    JOIN memory t ON t.id = l.target_memory_id
                LOOP
                    -- AGE requires cypher's third argument to be a prepared-statement parameter,
                    -- so values are interpolated. Uuids are %L (charset-safe). relation/reason
                    -- are Cypher-escaped then injected with %s — %L emits SQL E'...' / doubled
                    -- quotes, which are not Cypher string syntax.
                    IF position('$' || 'age_copy' || '$' in rec.relation) > 0
                       OR position('$q$' in rec.relation) > 0
                       OR position('$cypher$' in rec.relation) > 0
                       OR position('$' || 'age_copy' || '$' in rec.reason) > 0
                       OR position('$q$' in rec.reason) > 0
                       OR position('$cypher$' in rec.reason) > 0 THEN
                        RAISE EXCEPTION
                            'memory_link copy refused: relation or reason contains a reserved dollar-tag (source %, relation %)',
                            rec.source_uuid, rec.relation;
                    END IF;

                    EXECUTE format(
                        $q$SELECT v FROM ag_catalog.cypher('memory_graph', $cypher$
                            MERGE (s:Memory {memory_uuid: %L})
                            MERGE (t:Memory {memory_uuid: %L})
                            CREATE (s)-[:LINKS {relation: %s, reason: %s}]->(t)
                            RETURN 1
                        $cypher$) AS (v agtype)$q$,
                        rec.source_uuid::text,
                        rec.target_uuid::text,
                        '''' || replace(replace(replace(replace(replace(rec.relation,
                            E'\\', E'\\\\'), '''', E'\\'''), chr(10), E'\\n'), chr(13), E'\\r'), chr(9), E'\\t') || '''',
                        '''' || replace(replace(replace(replace(replace(rec.reason,
                            E'\\', E'\\\\'), '''', E'\\'''), chr(10), E'\\n'), chr(13), E'\\r'), chr(9), E'\\t') || '''');
                END LOOP;
            END
            $age_copy$;
            """);

        migrationBuilder.DropTable(name: "memory_link");

        migrationBuilder.Sql(
            """
            CREATE FUNCTION public.memory_graph_cascade() RETURNS trigger AS $fn$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'age') THEN
                    RAISE EXCEPTION 'Apache AGE is required to delete a memory';
                END IF;

                EXECUTE format(
                    $q$SELECT v FROM ag_catalog.cypher('memory_graph', $cypher$
                        MATCH (v:Memory {memory_uuid: %L})
                        DETACH DELETE v
                        RETURN 1
                    $cypher$) AS (v agtype)$q$,
                    OLD.uuid::text);

                RETURN OLD;
            END;
            $fn$ LANGUAGE plpgsql;
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_memory_graph_cascade
            BEFORE DELETE ON memory
            FOR EACH ROW EXECUTE FUNCTION public.memory_graph_cascade();
            """);

        // Unused foundation elabels — relation is a property on LINKS (open vocabulary).
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_drop_elabels$
            DECLARE
                graph_oid oid;
                edge_label text;
            BEGIN
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                FOREACH edge_label IN ARRAY ARRAY['depends_on', 'relates_to', 'contradicts', 'supersedes', 'implements'] LOOP
                    IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = edge_label) THEN
                        EXECUTE format('SELECT ag_catalog.drop_label(%L, %L)', 'memory_graph', edge_label);
                    END IF;
                END LOOP;
            END
            $age_drop_elabels$;
            """,
            suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_memory_graph_cascade ON memory;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.memory_graph_cascade();");

        migrationBuilder.CreateTable(
            name: "memory_link",
            columns: table => new
            {
                source_memory_id = table.Column<long>(type: "bigint", nullable: false),
                target_memory_id = table.Column<long>(type: "bigint", nullable: false),
                relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                reason = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_memory_link", x => new { x.source_memory_id, x.target_memory_id, x.relation });
                table.ForeignKey(
                    name: "FK_memory_link_memory_source_memory_id",
                    column: x => x.source_memory_id,
                    principalTable: "memory",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_memory_link_memory_target_memory_id",
                    column: x => x.target_memory_id,
                    principalTable: "memory",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_memory_link_target_memory_id",
            table: "memory_link",
            column: "target_memory_id");

        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_restore_guard$
            DECLARE
                conflict record;
            BEGIN
                SELECT d.source_uuid, d.target_uuid, d.relation
                INTO conflict
                FROM (
                    SELECT DISTINCT
                        trim(both '"' from s::text) AS source_uuid,
                        trim(both '"' from t::text) AS target_uuid,
                        rel::text::jsonb #>> '{}' AS relation,
                        reason::text::jsonb #>> '{}' AS reason
                    FROM ag_catalog.cypher('memory_graph', $cypher$
                        MATCH (s:Memory)-[e:LINKS]->(t:Memory)
                        RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
                    $cypher$) AS (s agtype, t agtype, rel agtype, reason agtype)
                ) d
                GROUP BY d.source_uuid, d.target_uuid, d.relation
                HAVING count(*) > 1
                LIMIT 1;

                IF FOUND THEN
                    RAISE EXCEPTION
                        'memory_link restore refused: race-created same-triple edges with differing reasons need manual resolution (source %, target %, relation %)',
                        conflict.source_uuid, conflict.target_uuid, conflict.relation;
                END IF;
            END
            $age_restore_guard$;
            """);

        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            INSERT INTO memory_link (source_memory_id, target_memory_id, relation, reason)
            SELECT s.id, t.id, r.relation, r.reason
            FROM (
                SELECT DISTINCT
                    trim(both '"' from s::text) AS source_uuid,
                    trim(both '"' from t::text) AS target_uuid,
                    rel::text::jsonb #>> '{}' AS relation,
                    reason::text::jsonb #>> '{}' AS reason
                FROM ag_catalog.cypher('memory_graph', $$
                    MATCH (s:Memory)-[e:LINKS]->(t:Memory)
                    RETURN s.memory_uuid, t.memory_uuid, e.relation, e.reason
                $$) AS (s agtype, t agtype, rel agtype, reason agtype)
            ) r
            JOIN memory s ON s.uuid = r.source_uuid::uuid
            JOIN memory t ON t.uuid = r.target_uuid::uuid;
            """);

        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;
            DO $age_down$
            DECLARE
                graph_oid oid;
                edge_label text;
            BEGIN
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';

                PERFORM v FROM ag_catalog.cypher('memory_graph', $cypher$
                    MATCH (s:Memory)-[e:LINKS]->(t:Memory)
                    DELETE e
                    RETURN 1
                $cypher$) AS (v agtype);

                PERFORM v FROM ag_catalog.cypher('memory_graph', $cypher$
                    MATCH (v:Memory)
                    DELETE v
                    RETURN 1
                $cypher$) AS (v agtype);

                IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'LINKS') THEN
                    EXECUTE format('SELECT ag_catalog.drop_label(%L, %L)', 'memory_graph', 'LINKS');
                END IF;

                FOREACH edge_label IN ARRAY ARRAY['depends_on', 'relates_to', 'contradicts', 'supersedes', 'implements'] LOOP
                    IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = edge_label) THEN
                        EXECUTE format('SELECT ag_catalog.create_elabel(%L, %L)', 'memory_graph', edge_label);
                    END IF;
                END LOOP;
            END
            $age_down$;
            """,
            suppressTransaction: true);
    }
}
