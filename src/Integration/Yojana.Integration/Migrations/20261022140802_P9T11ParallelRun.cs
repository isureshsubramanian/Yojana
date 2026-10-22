using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T11ParallelRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "explained_at",
                schema: "integration",
                table: "reconciliation_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "explained_by",
                schema: "integration",
                table: "reconciliation_run",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "explanation_note",
                schema: "integration",
                table: "reconciliation_run",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "month_end_close_on",
                schema: "integration",
                table: "parallel_run_plan",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "rollback_started_at",
                schema: "migration",
                table: "migration_apply_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "go_live_record",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    issued_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    snapshot_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_go_live_record", x => new { x.tenant_id, x.record_id });
                });

            migrationBuilder.CreateTable(
                name: "parallel_run_handover",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    planned_on = table.Column<DateOnly>(type: "date", nullable: true),
                    owned_since = table.Column<DateOnly>(type: "date", nullable: true),
                    evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    confirmed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parallel_run_handover", x => new { x.tenant_id, x.connection_id, x.kind });
                });

            migrationBuilder.CreateIndex(
                name: "ix_go_live_record_tenant_id_connection_id_issued_at",
                schema: "integration",
                table: "go_live_record",
                columns: new[] { "tenant_id", "connection_id", "issued_at" });

            migrationBuilder.AddTenantPolicy("integration", "parallel_run_handover");
            migrationBuilder.AddTenantPolicy("integration", "go_live_record");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "go_live_record");
            migrationBuilder.RemoveTenantPolicy("integration", "parallel_run_handover");

            migrationBuilder.DropTable(
                name: "go_live_record",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "parallel_run_handover",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "explained_at",
                schema: "integration",
                table: "reconciliation_run");

            migrationBuilder.DropColumn(
                name: "explained_by",
                schema: "integration",
                table: "reconciliation_run");

            migrationBuilder.DropColumn(
                name: "explanation_note",
                schema: "integration",
                table: "reconciliation_run");

            migrationBuilder.DropColumn(
                name: "month_end_close_on",
                schema: "integration",
                table: "parallel_run_plan");

            migrationBuilder.DropColumn(
                name: "rollback_started_at",
                schema: "migration",
                table: "migration_apply_run");
        }
    }
}
