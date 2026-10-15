using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the <c>ABOUT</c> edge label <c>(Memory)-[:ABOUT]->(Ticket)</c> (HLD-003 LADR-09) and the D1
/// guard on releasing the last owner of a referenced ticket.
/// </summary>
[DbContext(typeof(SmoothAiProductContextMemoryDbContext))]
[Migration("20261015100000_AddAboutReference")]
[ExcludeFromCodeCoverage]
public sealed class AddAboutReference : Migration
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
                IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'ABOUT') THEN
                    PERFORM ag_catalog.create_elabel('memory_graph', 'ABOUT');
                END IF;
            END
            $labels$;
            """, suppressTransaction: true);

        migrationBuilder.Sql(
            """
            SELECT pg_advisory_xact_lock(734921, 1);
            LOCK TABLE public.memory_group IN SHARE ROW EXCLUSIVE MODE NOWAIT;

            -- Count the incoming ABOUT edges for a ticket. The read path and the D1 guard both need
            -- this; a count() over an empty match still yields one row of 0 (LADR-06 property tests).
            CREATE OR REPLACE FUNCTION public.ticket_graph_about_count(t_provider text, t_key text)
            RETURNS bigint AS $fn$
            DECLARE n text;
            BEGIN
                SELECT v::text INTO n
                FROM ag_catalog.cypher('memory_graph',
                    'MATCH ()-[e:ABOUT]->(v:Ticket) WHERE v.provider = ' || to_json(t_provider)::text
                    || ' AND v.key = ' || to_json(t_key)::text || ' RETURN count(e)') AS (v agtype);
                RETURN COALESCE((regexp_replace(COALESCE(n, '0'), '::.*$', ''))::bigint, 0);
            END;
            $fn$ LANGUAGE plpgsql;

            -- D1: a referenced ticket keeps its vertex. When the last owner releases a ticket that still
            -- has incoming ABOUT edges, refuse the release (raise, like the ownership-conflict path) so a
            -- memory -> ticket reference is never lost silently. The guard runs before the DETACH DELETE.
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
                            IF public.ticket_graph_about_count(ticket->>'provider', ticket->>'key') > 0 THEN
                                RAISE EXCEPTION 'Ticket % cannot be released: it has memory references',
                                    ticket->>'provider' || ':' || ticket->>'key' USING ERRCODE = '23505';
                            END IF;
                            PERFORM public.ticket_graph_cypher(
                                format('MATCH (v:Ticket) WHERE v.provider = %s AND v.key = %s DETACH DELETE v RETURN 1',
                                    to_json(ticket->>'provider')::text, to_json(ticket->>'key')::text));
                        END IF;
                    END LOOP;
                END IF;
                RETURN NULL;
            END;
            $fn$ LANGUAGE plpgsql;
            """, suppressTransaction: true);
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
                PERFORM pg_advisory_xact_lock(734921, 1);
                LOCK TABLE public.memory_group IN SHARE ROW EXCLUSIVE MODE NOWAIT;
                -- Revert the membership trigger to its pre-D1 form (identity maintenance only).
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
                DROP FUNCTION IF EXISTS public.ticket_graph_about_count(text, text);
                SELECT graphid INTO graph_oid FROM ag_catalog.ag_graph WHERE name = 'memory_graph';
                IF EXISTS (SELECT 1 FROM ag_catalog.ag_label WHERE graph = graph_oid AND name = 'ABOUT') THEN
                    EXECUTE 'SELECT v FROM ag_catalog.cypher(''memory_graph'', $about$MATCH ()-[e:ABOUT]->() DELETE e RETURN 1$about$) AS (v agtype)';
                    PERFORM ag_catalog.drop_label('memory_graph', 'ABOUT');
                END IF;
            END
            $down$;
            """, suppressTransaction: true);
    }
}
