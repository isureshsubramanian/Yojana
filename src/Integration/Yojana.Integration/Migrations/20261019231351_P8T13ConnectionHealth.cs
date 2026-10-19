using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T13ConnectionHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_manual_outbound_task_tenant_id_dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropIndex(
                name: "ux_manual_outbound_task_tenant_id_outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_status",
                schema: "integration",
                table: "connection");

            migrationBuilder.AlterColumn<Guid>(
                name: "outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "canonical_message",
                schema: "integration",
                table: "connection_poll_watermark",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stream_state",
                schema: "integration",
                table: "connection_notification_watchdog",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "read_only",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "retired_at",
                schema: "integration",
                table: "connection_credential",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_smoke_read_at",
                schema: "integration",
                table: "connection",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "last_smoke_read_succeeded",
                schema: "integration",
                table: "connection",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "registration_completed",
                schema: "integration",
                table: "connection",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "registration_request_id",
                schema: "integration",
                table: "connection",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "connection_health",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    is_live = table.Column<bool>(type: "bit", nullable: false),
                    last_inbound_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    checked_up_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    notification_state = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    received_today = table.Column<long>(type: "bigint", nullable: false),
                    received_last30_days = table.Column<long>(type: "bigint", nullable: false),
                    inbound_count_date_utc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    completed_inbound_days_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    outbox_depth = table.Column<int>(type: "int", nullable: false),
                    oldest_waiting_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    max_outbox_age_seconds = table.Column<int>(type: "int", nullable: false),
                    open_dead_letters = table.Column<int>(type: "int", nullable: false),
                    open_manual_tasks = table.Column<int>(type: "int", nullable: false),
                    oldest_dead_letter_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_daily_check_result = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    last_daily_check_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    daily_check_differences = table.Column<int>(type: "int", nullable: false),
                    calls_used = table.Column<int>(type: "int", nullable: false),
                    calls_cap = table.Column<int>(type: "int", nullable: true),
                    call_alert_percent = table.Column<int>(type: "int", nullable: false),
                    credential_replace_by = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    credential_warn_days = table.Column<int>(type: "int", nullable: false),
                    last_error_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    last_error_message = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    record_types_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_health", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_connection_health_status", "status IN ('healthy','degraded','down','paused','archived')");
                });

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "dead_letter_item_id" },
                unique: true,
                filter: "[dead_letter_item_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "outbound_command_id" },
                unique: true,
                filter: "[outbound_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_message_tenant_id_connection_id_accepted_at_message_type",
                schema: "integration",
                table: "inbound_message",
                columns: new[] { "tenant_id", "connection_id", "accepted_at", "message_type" });

            migrationBuilder.CreateIndex(
                name: "ux_connection_tenant_id_registration_request_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "registration_request_id" },
                unique: true,
                filter: "[registration_request_id] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_status",
                schema: "integration",
                table: "connection",
                sql: "status IN ('active','paused','registering','archived')");

            migrationBuilder.CreateIndex(
                name: "ix_connection_health_tenant_id_status_connection_id",
                schema: "integration",
                table: "connection_health",
                columns: new[] { "tenant_id", "status", "connection_id" });
            migrationBuilder.AddTenantPolicy("integration", "connection_health");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connection_health");
            migrationBuilder.DropTable(
                name: "connection_health",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ux_manual_outbound_task_tenant_id_dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropIndex(
                name: "ux_manual_outbound_task_tenant_id_outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropIndex(
                name: "ix_inbound_message_tenant_id_connection_id_accepted_at_message_type",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropIndex(
                name: "ux_connection_tenant_id_registration_request_id",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_status",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "canonical_message",
                schema: "integration",
                table: "connection_poll_watermark");

            migrationBuilder.DropColumn(
                name: "stream_state",
                schema: "integration",
                table: "connection_notification_watchdog");

            migrationBuilder.DropColumn(
                name: "read_only",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "retired_at",
                schema: "integration",
                table: "connection_credential");

            migrationBuilder.DropColumn(
                name: "last_smoke_read_at",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "last_smoke_read_succeeded",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "registration_completed",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "registration_request_id",
                schema: "integration",
                table: "connection");

            migrationBuilder.AlterColumn<Guid>(
                name: "outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_dead_letter_item_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "dead_letter_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_manual_outbound_task_tenant_id_outbound_command_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "outbound_command_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_status",
                schema: "integration",
                table: "connection",
                sql: "status IN ('active','archived')");
        }
    }
}
