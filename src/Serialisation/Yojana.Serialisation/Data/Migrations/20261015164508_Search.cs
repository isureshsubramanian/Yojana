using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_entry",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    title_mono = table.Column<bool>(type: "bit", nullable: false),
                    subtitle = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    search_text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    type_weight = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    metadata = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    href = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_entry", x => new { x.tenant_id, x.entity_type, x.entity_id });
                    table.CheckConstraint("ck_search_entry_metadata_json", "ISJSON(metadata) = 1");
                });

            migrationBuilder.CreateTable(
                name: "search_term",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    term = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_term", x => new { x.tenant_id, x.term, x.entity_type, x.entity_id, x.kind });
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_entry_tenant_id_updated_at",
                schema: "serialisation",
                table: "search_entry",
                columns: new[] { "tenant_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_search_term_tenant_id_entity_type_entity_id",
                schema: "serialisation",
                table: "search_term",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            // Full-text KEY INDEX: single-column and unique by SQL Server's rule, so outside the EF model (see SerialisationDbContext).
            migrationBuilder.Sql("CREATE UNIQUE NONCLUSTERED INDEX ux_search_entry_entry_id ON serialisation.search_entry (entry_id);");
            migrationBuilder.AddTenantPolicy("serialisation", "search_entry");
            migrationBuilder.AddTenantPolicy("serialisation", "search_term");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "search_term");
            migrationBuilder.RemoveTenantPolicy("serialisation", "search_entry");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.fulltext_indexes i JOIN sys.objects o ON o.object_id = i.object_id WHERE o.name = 'search_entry') DROP FULLTEXT INDEX ON serialisation.search_entry;");
            migrationBuilder.DropTable(
                name: "search_entry",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "search_term",
                schema: "serialisation");
        }
    }
}
