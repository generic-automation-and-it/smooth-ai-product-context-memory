using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmoothAiProductContextMemory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "initiative",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_initiative", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "label",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memory_group",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_dimension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    initiative_id = table.Column<long>(type: "bigint", nullable: false),
                    repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    repo_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tickets = table.Column<string>(type: "jsonb", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_group", x => x.id);
                    table.ForeignKey(
                        name: "FK_memory_group_initiative_initiative_id",
                        column: x => x.initiative_id,
                        principalTable: "initiative",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_description",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_description", x => x.id);
                    table.ForeignKey(
                        name: "FK_group_description_memory_group_group_id",
                        column: x => x.group_id,
                        principalTable: "memory_group",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    lineage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    subject_slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    facets = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory", x => x.id);
                    table.ForeignKey(
                        name: "FK_memory_memory_group_group_id",
                        column: x => x.group_id,
                        principalTable: "memory_group",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "memory_version",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    memory_id = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    content_summary = table.Column<string>(type: "text", nullable: false),
                    blob_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confidence = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sources = table.Column<string>(type: "jsonb", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_version", x => x.id);
                    table.ForeignKey(
                        name: "FK_memory_version_memory_memory_id",
                        column: x => x.memory_id,
                        principalTable: "memory",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "initiative",
                columns: new[] { "id", "description", "name", "status" },
                values: new object[] { 1L, "Default initiative for groups not yet assigned.", "to-be-decided", "active" });

            migrationBuilder.InsertData(
                table: "label",
                columns: new[] { "id", "name", "status" },
                values: new object[,]
                {
                    { 2L, "positioning", "active" },
                    { 3L, "architecture", "active" },
                    { 4L, "storage", "active" },
                    { 5L, "domain-model", "active" },
                    { 6L, "write-path", "active" },
                    { 7L, "retrieval", "active" },
                    { 8L, "security", "active" },
                    { 9L, "governance", "active" },
                    { 10L, "prior-art", "active" },
                    { 11L, "process", "active" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_description_group_id_version",
                table: "group_description",
                columns: new[] { "group_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_initiative_name",
                table: "initiative",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_label_name",
                table: "label",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_facets",
                table: "memory",
                column: "facets")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_memory_group_id_subject_slug",
                table: "memory",
                columns: new[] { "group_id", "subject_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_lineage_id",
                table: "memory",
                column: "lineage_id");

            migrationBuilder.CreateIndex(
                name: "IX_memory_tags",
                table: "memory",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_memory_uuid",
                table: "memory",
                column: "uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_group_initiative_id",
                table: "memory_group",
                column: "initiative_id");

            migrationBuilder.CreateIndex(
                name: "IX_memory_group_tickets",
                table: "memory_group",
                column: "tickets")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_memory_group_uuid",
                table: "memory_group",
                column: "uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_link_target_memory_id",
                table: "memory_link",
                column: "target_memory_id");

            migrationBuilder.CreateIndex(
                name: "IX_memory_version_kind",
                table: "memory_version",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "IX_memory_version_memory_id",
                table: "memory_version",
                column: "memory_id",
                unique: true,
                filter: "\"is_current\"");

            migrationBuilder.CreateIndex(
                name: "IX_memory_version_memory_id_version",
                table: "memory_version",
                columns: new[] { "memory_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_version_status",
                table: "memory_version",
                column: "status");

            // GIST over the validity range so @> now() is index-served. EF models the two columns,
            // not the constructed range, so the expression index is added here.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_memory_version_validity
                ON memory_version USING gist (tstzrange(valid_from, valid_until));
                """);

            // Full-text over the subject and claim text. Blob content is not indexable, so these
            // indexed columns are the whole search surface.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_memory_name_description_fts
                ON memory USING gin (to_tsvector('simple', name || ' ' || description));
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_memory_version_statement_summary_fts
                ON memory_version USING gin (to_tsvector('simple', statement || ' ' || content_summary));
                """);

            // Usage counts are derived, never maintained — a maintained counter drifts.
            migrationBuilder.Sql(
                """
                CREATE VIEW label_usage AS
                SELECT unnest(facets) AS name, count(*) AS uses
                FROM memory
                GROUP BY 1;
                """);

            // Append-only history enforced by the database, not by convention. A future change that
            // moves history into a JSONB array would lose concurrent appends silently; the trigger
            // makes the rule explicit. Corrections are new versions, never updates.
            // The one permitted UPDATE is a version bump: transferring the current pointer
            // (is_current) with all content unchanged. Everything else raises. A session may opt
            // into a cascade delete of history by setting app.allow_history_delete='true'.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.append_only_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_TABLE_NAME = 'memory_version' AND TG_OP = 'UPDATE' THEN
                        -- Only a version-bump pointer transfer is legal; content is immutable.
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

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_memory_version_append_only
                BEFORE UPDATE OR DELETE ON memory_version
                FOR EACH ROW EXECUTE FUNCTION public.append_only_guard();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_group_description_append_only
                BEFORE UPDATE OR DELETE ON group_description
                FOR EACH ROW EXECUTE FUNCTION public.append_only_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the custom database objects before the tables they depend on.
            migrationBuilder.Sql("DROP VIEW IF EXISTS label_usage;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_version_validity;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_name_description_fts;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_memory_version_statement_summary_fts;");

            migrationBuilder.DropTable(
                name: "group_description");

            migrationBuilder.DropTable(
                name: "label");

            migrationBuilder.DropTable(
                name: "memory_link");

            migrationBuilder.DropTable(
                name: "memory_version");

            migrationBuilder.DropTable(
                name: "memory");

            migrationBuilder.DropTable(
                name: "memory_group");

            migrationBuilder.DropTable(
                name: "initiative");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.append_only_guard();");
        }
    }
}
