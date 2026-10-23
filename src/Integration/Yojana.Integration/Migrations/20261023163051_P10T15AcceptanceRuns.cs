using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T15AcceptanceRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connector_acceptance_run",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_acceptance_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    sandbox_label = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    started_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_acceptance_run", x => new { x.tenant_id, x.connector_acceptance_run_id });
                    table.CheckConstraint("ck_connector_acceptance_run_status", "status IN ('open','passed','failed')");
                });

            migrationBuilder.CreateTable(
                name: "connector_acceptance_import",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_acceptance_import_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_acceptance_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    connector_code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    scenario_count = table.Column<int>(type: "int", nullable: false),
                    imported_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    imported_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_acceptance_import", x => new { x.tenant_id, x.connector_acceptance_import_id });
                    table.ForeignKey(
                        name: "fk_connector_acceptance_import_tenant_id_connector_acceptance_run_id",
                        columns: x => new { x.tenant_id, x.connector_acceptance_run_id },
                        principalSchema: "integration",
                        principalTable: "connector_acceptance_run",
                        principalColumns: new[] { "tenant_id", "connector_acceptance_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connector_acceptance_item",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_acceptance_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_acceptance_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(140)", unicode: false, maxLength: 140, nullable: false),
                    kind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    label = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    result = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    evidence = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    marked_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    marked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connector_acceptance_item", x => new { x.tenant_id, x.connector_acceptance_item_id });
                    table.CheckConstraint("ck_connector_acceptance_item_kind", "kind IN ('fact','edge_scenario','outbound_command')");
                    table.CheckConstraint("ck_connector_acceptance_item_result", "result IN ('pending','passed','failed','not_applicable')");
                    table.ForeignKey(
                        name: "fk_connector_acceptance_item_tenant_id_connector_acceptance_run_id",
                        columns: x => new { x.tenant_id, x.connector_acceptance_run_id },
                        principalSchema: "integration",
                        principalTable: "connector_acceptance_run",
                        principalColumns: new[] { "tenant_id", "connector_acceptance_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_connector_acceptance_import_tenant_id_connector_acceptance_run_id_imported_at_connector_acceptance_import_id",
                schema: "integration",
                table: "connector_acceptance_import",
                columns: new[] { "tenant_id", "connector_acceptance_run_id", "imported_at", "connector_acceptance_import_id" });

            migrationBuilder.CreateIndex(
                name: "ux_connector_acceptance_import_tenant_id_content_hash",
                schema: "integration",
                table: "connector_acceptance_import",
                columns: new[] { "tenant_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_acceptance_item_tenant_id_connector_acceptance_run_id_kind_result_connector_acceptance_item_id",
                schema: "integration",
                table: "connector_acceptance_item",
                columns: new[] { "tenant_id", "connector_acceptance_run_id", "kind", "result", "connector_acceptance_item_id" });

            migrationBuilder.CreateIndex(
                name: "ux_connector_acceptance_item_tenant_id_connector_acceptance_run_id_code",
                schema: "integration",
                table: "connector_acceptance_item",
                columns: new[] { "tenant_id", "connector_acceptance_run_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_acceptance_run_tenant_id_connection_id_started_at_connector_acceptance_run_id",
                schema: "integration",
                table: "connector_acceptance_run",
                columns: new[] { "tenant_id", "connection_id", "started_at", "connector_acceptance_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_connector_acceptance_run_tenant_id_connection_id_status",
                schema: "integration",
                table: "connector_acceptance_run",
                columns: new[] { "tenant_id", "connection_id", "status" },
                unique: true,
                filter: "[status] = 'open'");

            migrationBuilder.AddTenantPolicy("integration", "connector_acceptance_run");
            migrationBuilder.AddTenantPolicy("integration", "connector_acceptance_item");
            migrationBuilder.AddTenantPolicy("integration", "connector_acceptance_import");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connector_acceptance_import");
            migrationBuilder.RemoveTenantPolicy("integration", "connector_acceptance_item");
            migrationBuilder.RemoveTenantPolicy("integration", "connector_acceptance_run");

            migrationBuilder.DropTable(
                name: "connector_acceptance_import",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connector_acceptance_item",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connector_acceptance_run",
                schema: "integration");
        }
    }
}
