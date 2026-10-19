using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T09Resequencer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ordering_wait_minutes",
                schema: "integration",
                table: "connection_gateway_setting",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "resequence_item",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    resequence_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    dependency_key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    held_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    hold_until = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    released_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    late = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resequence_item", x => new { x.tenant_id, x.resequence_item_id });
                    table.CheckConstraint("ck_resequence_status", "status IN ('held','released')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_gateway_ordering_wait",
                schema: "integration",
                table: "connection_gateway_setting",
                sql: "ordering_wait_minutes BETWEEN 0 AND 1440");

            migrationBuilder.CreateIndex(
                name: "ix_resequence_item_tenant_id_connection_id_dependency_key_status_event_time_resequence_item_id",
                schema: "integration",
                table: "resequence_item",
                columns: new[] { "tenant_id", "connection_id", "dependency_key", "status", "event_time", "resequence_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_resequence_item_tenant_id_status_hold_until_resequence_item_id",
                schema: "integration",
                table: "resequence_item",
                columns: new[] { "tenant_id", "status", "hold_until", "resequence_item_id" });

            migrationBuilder.CreateIndex(
                name: "ux_resequence_item_tenant_id_inbound_message_id",
                schema: "integration",
                table: "resequence_item",
                columns: new[] { "tenant_id", "inbound_message_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("integration", "resequence_item");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "resequence_item");
            migrationBuilder.DropTable(
                name: "resequence_item",
                schema: "integration");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_gateway_ordering_wait",
                schema: "integration",
                table: "connection_gateway_setting");

            migrationBuilder.DropColumn(
                name: "ordering_wait_minutes",
                schema: "integration",
                table: "connection_gateway_setting");
        }
    }
}
