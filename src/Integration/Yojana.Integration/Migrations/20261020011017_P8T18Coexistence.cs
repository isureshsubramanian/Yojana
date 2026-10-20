using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T18Coexistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coexistence_baseline",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    record_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    field_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    explaining_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coexistence_baseline", x => new { x.tenant_id, x.connection_id, x.record_type, x.record_key, x.field_name });
                });

            migrationBuilder.CreateTable(
                name: "coexistence_conflict",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    conflict_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    record_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    record_label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    field_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    baseline_value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    remote_value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    detected_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    decision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    decision_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    manual_task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coexistence_conflict", x => new { x.tenant_id, x.conflict_id });
                    table.CheckConstraint("ck_coexistence_conflict_status", "status IN ('open','awaiting_remote_revert','closed')");
                });

            migrationBuilder.CreateTable(
                name: "coexistence_scan_state",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    cursor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    pass_started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_pass_records = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coexistence_scan_state", x => new { x.tenant_id, x.connection_id, x.record_type });
                });

            migrationBuilder.CreateTable(
                name: "parallel_run_confirmation",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    confirmed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parallel_run_confirmation", x => new { x.tenant_id, x.connection_id, x.item_code });
                });

            migrationBuilder.CreateTable(
                name: "parallel_run_plan",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    days = table.Column<int>(type: "int", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    started_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parallel_run_plan", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_parallel_run_plan_days", "days IN (30,60,90)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_coexistence_conflict_tenant_id_connection_id_status_detected_at_conflict_id",
                schema: "integration",
                table: "coexistence_conflict",
                columns: new[] { "tenant_id", "connection_id", "status", "detected_at", "conflict_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coexistence_conflict_tenant_id_status_detected_at_conflict_id",
                schema: "integration",
                table: "coexistence_conflict",
                columns: new[] { "tenant_id", "status", "detected_at", "conflict_id" });

            migrationBuilder.CreateIndex(
                name: "ux_coexistence_conflict_tenant_id_connection_id_record_type_record_key_field_name",
                schema: "integration",
                table: "coexistence_conflict",
                columns: new[] { "tenant_id", "connection_id", "record_type", "record_key", "field_name" },
                unique: true,
                filter: "[status] <> 'closed'");

            migrationBuilder.AddTenantPolicy("integration", "coexistence_baseline");
            migrationBuilder.AddTenantPolicy("integration", "coexistence_conflict");
            migrationBuilder.AddTenantPolicy("integration", "coexistence_scan_state");
            migrationBuilder.AddTenantPolicy("integration", "parallel_run_plan");
            migrationBuilder.AddTenantPolicy("integration", "parallel_run_confirmation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "parallel_run_confirmation");
            migrationBuilder.RemoveTenantPolicy("integration", "parallel_run_plan");
            migrationBuilder.RemoveTenantPolicy("integration", "coexistence_scan_state");
            migrationBuilder.RemoveTenantPolicy("integration", "coexistence_conflict");
            migrationBuilder.RemoveTenantPolicy("integration", "coexistence_baseline");

            migrationBuilder.DropTable(
                name: "coexistence_baseline",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "coexistence_conflict",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "coexistence_scan_state",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "parallel_run_confirmation",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "parallel_run_plan",
                schema: "integration");
        }
    }
}
