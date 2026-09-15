using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20260914180000_AddTicketGraph")]
[ExcludeFromCodeCoverage]
public sealed class AddTicketGraph : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // AGE labels must commit before a later batch can address them. Retrying a partial migration is safe.
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
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Ticket') THEN
                    PERFORM ag_catalog.create_vlabel('memory_graph', 'Ticket');
                END IF;
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'TICKET_PARENT') THEN
                    PERFORM ag_catalog.create_elabel('memory_graph', 'TICKET_PARENT');
                END IF;
            END
            $labels$;
            """, suppressTransaction: true);

        migrationBuilder.Sql(
            """
            LOCK TABLE public.memory_group IN SHARE ROW EXCLUSIVE MODE;
            SELECT pg_advisory_xact_lock(734921, 1);

            CREATE INDEX IF NOT EXISTS ix_ticket_vertex_provider ON memory_graph."Ticket"
                USING hash (
                    ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, '"provider"'::ag_catalog.agtype]));
            CREATE INDEX IF NOT EXISTS ix_ticket_vertex_key ON memory_graph."Ticket"
                USING hash (
                    ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, '"key"'::ag_catalog.agtype]));
            CREATE INDEX IF NOT EXISTS ix_ticket_vertex_properties ON memory_graph."Ticket" USING gin (properties);

            CREATE OR REPLACE FUNCTION public.ticket_graph_cypher(query text) RETURNS void AS $fn$
            DECLARE tag text := '$ticket$';
            BEGIN
                WHILE position(tag in query) > 0 LOOP
                    tag := '$ticket_' || md5(tag) || '$';
                END LOOP;
                EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', ' || tag || query || tag || ') AS (v ag_catalog.agtype)';
            END;
            $fn$ LANGUAGE plpgsql;

            CREATE OR REPLACE FUNCTION public.ticket_graph_lock() RETURNS trigger AS $fn$
            BEGIN
                PERFORM pg_advisory_xact_lock(734921, 1);
                RETURN NULL;
            END;
            $fn$ LANGUAGE plpgsql;

            CREATE OR REPLACE FUNCTION public.ticket_graph_membership() RETURNS trigger AS $fn$
            DECLARE ticket jsonb;
            BEGIN
                PERFORM pg_advisory_xact_lock(734921, 1);
                IF TG_OP <> 'DELETE' THEN
                    FOR ticket IN SELECT DISTINCT value FROM jsonb_array_elements(NEW.tickets) LOOP
                        IF EXISTS (
                            SELECT 1 FROM public.memory_group g
                            WHERE g.id <> NEW.id AND EXISTS (
                                SELECT 1 FROM jsonb_array_elements(g.tickets) t
                                WHERE t->>'provider' = ticket->>'provider' AND t->>'key' = ticket->>'key')) THEN
                            RAISE EXCEPTION 'Ticket ownership conflicts' USING ERRCODE = '23505';
                        END IF;
                        PERFORM public.ticket_graph_cypher(
                            format('MERGE (v:Ticket {provider: %s, key: %s}) RETURN 1',
                                to_json(ticket->>'provider')::text, to_json(ticket->>'key')::text));
                    END LOOP;
                END IF;
                IF TG_OP <> 'INSERT' THEN
                    FOR ticket IN SELECT DISTINCT value FROM jsonb_array_elements(OLD.tickets) LOOP
                        IF NOT EXISTS (
                            SELECT 1 FROM public.memory_group g WHERE EXISTS (
                                SELECT 1 FROM jsonb_array_elements(g.tickets) t
                                WHERE t->>'provider' = ticket->>'provider' AND t->>'key' = ticket->>'key')) THEN
                            PERFORM public.ticket_graph_cypher(
                                format('MATCH (v:Ticket) WHERE v.provider = %s AND v.key = %s DETACH DELETE v RETURN 1',
                                    to_json(ticket->>'provider')::text, to_json(ticket->>'key')::text));
                        END IF;
                    END LOOP;
                END IF;
                RETURN NULL;
            END;
            $fn$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_ticket_graph_lock ON public.memory_group;
            CREATE TRIGGER trg_ticket_graph_lock BEFORE INSERT OR UPDATE OF tickets OR DELETE ON public.memory_group
                FOR EACH STATEMENT EXECUTE FUNCTION public.ticket_graph_lock();
            DROP TRIGGER IF EXISTS trg_ticket_graph_membership ON public.memory_group;
            CREATE TRIGGER trg_ticket_graph_membership AFTER INSERT OR UPDATE OF tickets OR DELETE ON public.memory_group
                FOR EACH ROW EXECUTE FUNCTION public.ticket_graph_membership();

            DO $backfill$
            DECLARE ticket record;
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM public.memory_group g CROSS JOIN LATERAL jsonb_array_elements(g.tickets) t
                    GROUP BY t->>'provider', t->>'key' HAVING count(DISTINCT g.id) > 1) THEN
                    RAISE EXCEPTION 'Ticket ownership conflicts' USING ERRCODE = '23505';
                END IF;
                FOR ticket IN
                    SELECT DISTINCT t->>'provider' AS provider, t->>'key' AS key
                    FROM public.memory_group g CROSS JOIN LATERAL jsonb_array_elements(g.tickets) t
                LOOP
                    PERFORM public.ticket_graph_cypher(
                        format('MERGE (v:Ticket {provider: %s, key: %s}) RETURN 1',
                            to_json(ticket.provider)::text, to_json(ticket.key)::text));
                END LOOP;
            END
            $backfill$;
            ANALYZE memory_graph."Ticket";
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
                RAISE WARNING 'Captured ticket hierarchy declarations will be lost; reapplying restores identities only';
                LOCK TABLE public.memory_group IN SHARE ROW EXCLUSIVE MODE;
                PERFORM pg_advisory_xact_lock(734921, 1);
                DROP TRIGGER IF EXISTS trg_ticket_graph_membership ON public.memory_group;
                DROP TRIGGER IF EXISTS trg_ticket_graph_lock ON public.memory_group;
                DROP FUNCTION IF EXISTS public.ticket_graph_membership();
                DROP FUNCTION IF EXISTS public.ticket_graph_lock();
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'TICKET_PARENT') THEN
                    EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $ticket$MATCH ()-[e:TICKET_PARENT]->() DELETE e RETURN 1$ticket$) AS (v ag_catalog.agtype)';
                    PERFORM ag_catalog.drop_label('memory_graph', 'TICKET_PARENT');
                END IF;
                IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'Ticket') THEN
                    EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $ticket$MATCH (v:Ticket) DELETE v RETURN 1$ticket$) AS (v ag_catalog.agtype)';
                    DROP INDEX IF EXISTS memory_graph.ix_ticket_vertex_provider;
                    DROP INDEX IF EXISTS memory_graph.ix_ticket_vertex_key;
                    DROP INDEX IF EXISTS memory_graph.ix_ticket_vertex_properties;
                    PERFORM ag_catalog.drop_label('memory_graph', 'Ticket');
                END IF;
                DROP FUNCTION IF EXISTS public.ticket_graph_cypher(text);
            END
            $down$;
            """, suppressTransaction: true);
    }
}
