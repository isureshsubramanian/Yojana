using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class Sync01StopState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "notification_control_action",
                schema: "integration",
                table: "connection",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "notification_control_attempt_count",
                schema: "integration",
                table: "connection",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "notification_control_failure_code",
                schema: "integration",
                table: "connection",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "notification_control_next_attempt_at",
                schema: "integration",
                table: "connection",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "notification_control_revision",
                schema: "integration",
                table: "connection",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "notification_control_state",
                schema: "integration",
                table: "connection",
                type: "varchar(24)",
                unicode: false,
                maxLength: 24,
                nullable: false,
                defaultValue: "not_supported");

            migrationBuilder.AddColumn<DateTime>(
                name: "sync_resumed_at",
                schema: "integration",
                table: "connection",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_stop_reason",
                schema: "integration",
                table: "connection",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sync_stopped_at",
                schema: "integration",
                table: "connection",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_stopped_by",
                schema: "integration",
                table: "connection",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_connection_tenant_id_notification_control_next_attempt_at_connection_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "notification_control_next_attempt_at", "connection_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_notification_control_action",
                schema: "integration",
                table: "connection",
                sql: "notification_control_action IS NULL OR notification_control_action IN ('suspend','resume')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_notification_control_state",
                schema: "integration",
                table: "connection",
                sql: "notification_control_state IN ('pending','held','not_held','not_supported')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_connection_tenant_id_notification_control_next_attempt_at_connection_id",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_notification_control_action",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_notification_control_state",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_action",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_attempt_count",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_failure_code",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_next_attempt_at",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_revision",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "notification_control_state",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "sync_resumed_at",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "sync_stop_reason",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "sync_stopped_at",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "sync_stopped_by",
                schema: "integration",
                table: "connection");
        }
    }
}
