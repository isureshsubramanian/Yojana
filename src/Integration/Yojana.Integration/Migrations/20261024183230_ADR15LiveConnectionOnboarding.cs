using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class ADR15LiveConnectionOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_error_code",
                schema: "integration",
                table: "connection_poll_watermark",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "master_data_full",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "service_certificate_sha256",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sync_start_configured_at",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "sync_start_date",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_start_mode",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_smoke_read_failure_code",
                schema: "integration",
                table: "connection",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_smoke_read_failure_detail",
                schema: "integration",
                table: "connection",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_gateway_sync_start_date",
                schema: "integration",
                table: "connection_gateway_setting",
                sql: "(sync_start_mode = 'date' AND sync_start_date IS NOT NULL) OR (sync_start_mode <> 'date' AND sync_start_date IS NULL) OR sync_start_mode IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_gateway_sync_start_mode",
                schema: "integration",
                table: "connection_gateway_setting",
                sql: "sync_start_mode IS NULL OR sync_start_mode IN ('today','date','full_history')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_gateway_sync_start_date",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_gateway_sync_start_mode",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "last_error_code",
                schema: "integration",
                table: "connection_poll_watermark");

            migrationBuilder.DropColumn(
                name: "master_data_full",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "service_certificate_sha256",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "sync_start_configured_at",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "sync_start_date",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "sync_start_mode",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "last_smoke_read_failure_code",
                schema: "integration",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "last_smoke_read_failure_detail",
                schema: "integration",
                table: "connection");
        }
    }
}
