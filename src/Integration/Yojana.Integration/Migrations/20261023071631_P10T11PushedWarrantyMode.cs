using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T11PushedWarrantyMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "native_warranty_push_state",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    remote_record_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    desired_module_status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    desired_warranty_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    includes_warranty_contract_fields = table.Column<bool>(type: "bit", nullable: false),
                    last_pushed_remote_status = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    last_pushed_warranty_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    outbound_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    allow_remote_overwrite_once = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_pushed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_native_warranty_push_state", x => new { x.tenant_id, x.connection_id, x.unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_native_warranty_push_state_tenant_id_connection_id_updated_at_unit_id",
                schema: "integration",
                table: "native_warranty_push_state",
                columns: new[] { "tenant_id", "connection_id", "updated_at", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_native_warranty_push_state_tenant_id_outbound_command_id",
                schema: "integration",
                table: "native_warranty_push_state",
                columns: new[] { "tenant_id", "outbound_command_id" });

            migrationBuilder.AddTenantPolicy("integration", "native_warranty_push_state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "native_warranty_push_state");

            migrationBuilder.DropTable(
                name: "native_warranty_push_state",
                schema: "integration");
        }
    }
}
