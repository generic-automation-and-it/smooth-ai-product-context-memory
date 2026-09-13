using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[ExcludeFromCodeCoverage]
public partial class AddGraphPropertyIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // AGE ships no property indexes — only Memory_pkey on the internal graph id and
        // LINKS_start_id_idx / LINKS_end_id_idx for edge traversal. Every anchor lookup was therefore
        // a sequential scan over the vertex table (LADR-06).
        //
        // The two Cypher forms compile to different predicates and need different indexes:
        //   MATCH (n:Memory) WHERE n.memory_uuid = 'x'  ->  agtype_access_operator(...)  -> btree
        //   MATCH (n:Memory {memory_uuid: 'x'})         ->  properties @> '{...}'        -> GIN
        // MERGE cannot be written as a WHERE clause, so both forms are in use and both are indexed.
        migrationBuilder.Sql(
            """
            LOAD 'age';
            SET search_path = ag_catalog, "$user", public;

            CREATE INDEX IF NOT EXISTS ix_memory_vertex_uuid
                ON memory_graph."Memory"
                USING btree (ag_catalog.agtype_access_operator(
                    VARIADIC ARRAY[properties, '"memory_uuid"'::ag_catalog.agtype]));

            CREATE INDEX IF NOT EXISTS ix_memory_vertex_properties
                ON memory_graph."Memory"
                USING gin (properties);

            CREATE INDEX IF NOT EXISTS ix_memory_links_relation
                ON memory_graph."LINKS"
                USING btree (ag_catalog.agtype_access_operator(
                    VARIADIC ARRAY[properties, '"relation"'::ag_catalog.agtype]));

            ANALYZE memory_graph."Memory";
            ANALYZE memory_graph."LINKS";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS memory_graph.ix_memory_links_relation;
            DROP INDEX IF EXISTS memory_graph.ix_memory_vertex_properties;
            DROP INDEX IF EXISTS memory_graph.ix_memory_vertex_uuid;
            """);
    }
}
