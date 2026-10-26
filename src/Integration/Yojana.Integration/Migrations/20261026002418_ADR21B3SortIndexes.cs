using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class ADR21B3SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_relay_tenant_id_connection_id_display_name_relay_id",
                schema: "integration",
                table: "relay",
                columns: new[] { "tenant_id", "connection_id", "display_name", "relay_id" });

            migrationBuilder.CreateIndex(
                name: "ix_relay_tenant_id_connection_id_status_relay_id",
                schema: "integration",
                table: "relay",
                columns: new[] { "tenant_id", "connection_id", "status", "relay_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_item_code_item_id",
                schema: "migration",
                table: "migration_delta_item",
                columns: new[] { "tenant_id", "run_id", "item_code", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_serial_item_id",
                schema: "migration",
                table: "migration_delta_item",
                columns: new[] { "tenant_id", "run_id", "serial", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_unit_tenant_id_item_code_plan_unit_id",
                schema: "migration",
                table: "migration_plan_unit",
                columns: new[] { "tenant_id", "item_code", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_manual_outbound_task_tenant_id_status_desk_manual_outbound_task_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "status", "desk", "manual_outbound_task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_tenant_id_display_label_connection_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "display_label", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_tenant_id_status_connection_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "status", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_run_tenant_id_status_erp_change_run_id",
                schema: "integration",
                table: "erp_change_run",
                columns: new[] { "tenant_id", "status", "erp_change_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coexistence_conflict_tenant_id_detected_at_conflict_id",
                schema: "integration",
                table: "coexistence_conflict",
                columns: new[] { "tenant_id", "detected_at", "conflict_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coexistence_conflict_tenant_id_status_conflict_id",
                schema: "integration",
                table: "coexistence_conflict",
                columns: new[] { "tenant_id", "status", "conflict_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relay_tenant_id_connection_id_display_name_relay_id",
                schema: "integration",
                table: "relay");

            migrationBuilder.DropIndex(
                name: "ix_relay_tenant_id_connection_id_status_relay_id",
                schema: "integration",
                table: "relay");

            migrationBuilder.DropIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_item_code_item_id",
                schema: "migration",
                table: "migration_delta_item");

            migrationBuilder.DropIndex(
                name: "ix_migration_delta_item_tenant_id_run_id_serial_item_id",
                schema: "migration",
                table: "migration_delta_item");

            migrationBuilder.DropIndex(
                name: "ix_migration_plan_unit_tenant_id_item_code_plan_unit_id",
                schema: "migration",
                table: "migration_plan_unit");

            migrationBuilder.DropIndex(
                name: "ix_manual_outbound_task_tenant_id_status_desk_manual_outbound_task_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropIndex(
                name: "ix_connection_tenant_id_display_label_connection_id",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropIndex(
                name: "ix_connection_tenant_id_status_connection_id",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropIndex(
                name: "ix_erp_change_run_tenant_id_status_erp_change_run_id",
                schema: "integration",
                table: "erp_change_run");

            migrationBuilder.DropIndex(
                name: "ix_coexistence_conflict_tenant_id_detected_at_conflict_id",
                schema: "integration",
                table: "coexistence_conflict");

            migrationBuilder.DropIndex(
                name: "ix_coexistence_conflict_tenant_id_status_conflict_id",
                schema: "integration",
                table: "coexistence_conflict");
        }
    }
}
