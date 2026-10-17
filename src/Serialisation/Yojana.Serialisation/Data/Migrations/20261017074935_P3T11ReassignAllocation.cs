using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T11ReassignAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "serial_allocation_override",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    scheme = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    serial_ordinal = table.Column<long>(type: "bigint", nullable: false),
                    serial_value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    serial_range_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    production_order_line = table.Column<int>(type: "int", nullable: true),
                    last_correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    commissioned_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_serial_allocation_override", x => new { x.tenant_id, x.scheme, x.serial_ordinal });
                });

            migrationBuilder.CreateTable(
                name: "serial_allocation_reassignment",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reassignment_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    pair = table.Column<int>(type: "int", nullable: false),
                    mode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    serial_range_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    scheme = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    serial_ordinal = table.Column<long>(type: "bigint", nullable: false),
                    serial_value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    previous_production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    previous_production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    previous_production_order_line = table.Column<int>(type: "int", nullable: true),
                    new_production_order_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    new_production_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    new_production_order_line = table.Column<int>(type: "int", nullable: true),
                    event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attributes = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_serial_allocation_reassignment", x => new { x.tenant_id, x.reassignment_id });
                    table.CheckConstraint("ck_serial_allocation_reassignment_attributes_json", "ISJSON(attributes) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_serial_allocation_override_tenant_id_commissioned_unit_id",
                schema: "serialisation",
                table: "serial_allocation_override",
                columns: new[] { "tenant_id", "commissioned_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_allocation_override_tenant_id_production_order_type_production_order_id_production_order_line",
                schema: "serialisation",
                table: "serial_allocation_override",
                columns: new[] { "tenant_id", "production_order_type", "production_order_id", "production_order_line" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_allocation_override_tenant_id_serial_range_id_serial_ordinal",
                schema: "serialisation",
                table: "serial_allocation_override",
                columns: new[] { "tenant_id", "serial_range_id", "serial_ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_allocation_reassignment_tenant_id_serial_range_id_serial_ordinal_event_time",
                schema: "serialisation",
                table: "serial_allocation_reassignment",
                columns: new[] { "tenant_id", "serial_range_id", "serial_ordinal", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ux_serial_allocation_reassignment_tenant_id_command_id_scheme_serial_ordinal",
                schema: "serialisation",
                table: "serial_allocation_reassignment",
                columns: new[] { "tenant_id", "command_id", "scheme", "serial_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_serial_allocation_reassignment_tenant_id_correction_id_scheme_serial_ordinal",
                schema: "serialisation",
                table: "serial_allocation_reassignment",
                columns: new[] { "tenant_id", "correction_id", "scheme", "serial_ordinal" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "serial_allocation_override");
            migrationBuilder.AddTenantPolicy("serialisation", "serial_allocation_reassignment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "serial_allocation_override");
            migrationBuilder.RemoveTenantPolicy("serialisation", "serial_allocation_reassignment");

            migrationBuilder.DropTable(
                name: "serial_allocation_override",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "serial_allocation_reassignment",
                schema: "serialisation");
        }
    }
}
