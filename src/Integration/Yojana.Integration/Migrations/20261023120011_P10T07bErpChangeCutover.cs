using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T07bErpChangeCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                schema: "integration",
                table: "erp_change_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "cutover_at",
                schema: "integration",
                table: "erp_change_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "dual_run_started_at",
                schema: "integration",
                table: "erp_change_run",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_outbound_mode",
                schema: "integration",
                table: "erp_change_run",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_outbound_mode",
                schema: "integration",
                table: "erp_change_run",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "erp_change_checklist_item",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_checklist_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    completed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_checklist_item", x => new { x.tenant_id, x.erp_change_checklist_item_id });
                    table.CheckConstraint("ck_erp_change_checklist_item_state", "state IN ('waiting','passed','failed')");
                    table.ForeignKey(
                        name: "fk_erp_change_checklist_item_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_daily_check",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_daily_check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source_reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    target_reconciliation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    new_divergences = table.Column<int>(type: "int", nullable: false),
                    aged_open_divergences = table.Column<int>(type: "int", nullable: false),
                    clean = table.Column<bool>(type: "bit", nullable: false),
                    checked_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    checked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_daily_check", x => new { x.tenant_id, x.erp_change_daily_check_id });
                    table.ForeignKey(
                        name: "fk_erp_change_daily_check_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "erp_change_divergence",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_divergence_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    subject_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    subject_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    subject_ref = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    source_value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    target_value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    source_inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    target_inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    stock_difference_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    first_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    review_due_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    resolution = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    resolution_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    resolution_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    resolved_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_divergence", x => new { x.tenant_id, x.erp_change_divergence_id });
                    table.CheckConstraint("ck_erp_change_divergence_status", "status IN ('open','resolved')");
                    table.ForeignKey(
                        name: "fk_erp_change_divergence_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_checklist_item_tenant_id_erp_change_run_id_code",
                schema: "integration",
                table: "erp_change_checklist_item",
                columns: new[] { "tenant_id", "erp_change_run_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_daily_check_tenant_id_erp_change_run_id_business_date",
                schema: "integration",
                table: "erp_change_daily_check",
                columns: new[] { "tenant_id", "erp_change_run_id", "business_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_divergence_tenant_id_erp_change_run_id_status_kind_first_seen_at_erp_change_divergence_id",
                schema: "integration",
                table: "erp_change_divergence",
                columns: new[] { "tenant_id", "erp_change_run_id", "status", "kind", "first_seen_at", "erp_change_divergence_id" });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_divergence_tenant_id_erp_change_run_id_kind_subject_type_subject_ref",
                schema: "integration",
                table: "erp_change_divergence",
                columns: new[] { "tenant_id", "erp_change_run_id", "kind", "subject_type", "subject_ref" },
                unique: true);

            foreach (var table in new[]
                     {
                         "erp_change_checklist_item", "erp_change_daily_check", "erp_change_divergence",
                     })
                migrationBuilder.AddTenantPolicy("integration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
                     {
                         "erp_change_divergence", "erp_change_daily_check", "erp_change_checklist_item",
                     })
                migrationBuilder.RemoveTenantPolicy("integration", table);

            migrationBuilder.DropTable(
                name: "erp_change_checklist_item",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_daily_check",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "erp_change_divergence",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "cutover_at",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "dual_run_started_at",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "source_outbound_mode",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropColumn(
                name: "target_outbound_mode",
                schema: "integration",
                table: "erp_change_run");
        }
    }
}
