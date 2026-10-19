using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T07Relay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbound_command_tenant_id_connection_id_status_created_at_outbound_command_id",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                schema: "integration",
                table: "outbound_command",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                schema: "integration",
                table: "outbound_command",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_change_marker",
                schema: "integration",
                table: "outbound_command",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_external_id",
                schema: "integration",
                table: "outbound_command",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_attempt_at",
                schema: "integration",
                table: "outbound_command",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_failure",
                schema: "integration",
                table: "outbound_command",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "locked_until",
                schema: "integration",
                table: "outbound_command",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                schema: "integration",
                table: "outbound_command",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                schema: "integration",
                table: "outbound_command",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "integration",
                table: "outbound_command",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.CreateTable(
                name: "external_id_map",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    module_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    valid_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    valid_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    natural_key_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    mapping_source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    mapped_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    mapped_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_id_map", x => new { x.tenant_id, x.connection_id, x.entity_type, x.external_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_command_tenant_id_connection_id_status_next_attempt_at_created_at_outbound_command_id",
                schema: "integration",
                table: "outbound_command",
                columns: new[] { "tenant_id", "connection_id", "status", "next_attempt_at", "created_at", "outbound_command_id" });

            migrationBuilder.CreateIndex(
                name: "ix_external_id_map_tenant_id_module_id_entity_type_valid_to",
                schema: "integration",
                table: "external_id_map",
                columns: new[] { "tenant_id", "module_id", "entity_type", "valid_to" });

            migrationBuilder.AddTenantPolicy("integration", "external_id_map");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "external_id_map");
            migrationBuilder.DropTable(
                name: "external_id_map",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ix_outbound_command_tenant_id_connection_id_status_next_attempt_at_created_at_outbound_command_id",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "completed_at",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "erp_change_marker",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "erp_external_id",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "last_attempt_at",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "last_failure",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "locked_until",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "outcome",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_command_tenant_id_connection_id_status_created_at_outbound_command_id",
                schema: "integration",
                table: "outbound_command",
                columns: new[] { "tenant_id", "connection_id", "status", "created_at", "outbound_command_id" });
        }
    }
}
