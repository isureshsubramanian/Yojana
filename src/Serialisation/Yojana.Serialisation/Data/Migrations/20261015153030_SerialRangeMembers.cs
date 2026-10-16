using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class SerialRangeMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "serial_range_member",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    range_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ordinal = table.Column<long>(type: "bigint", nullable: false),
                    value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    commissioned_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_serial_range_member", x => new { x.tenant_id, x.range_id, x.ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_member_tenant_id_commissioned_unit_id",
                schema: "serialisation",
                table: "serial_range_member",
                columns: new[] { "tenant_id", "commissioned_unit_id" });

            migrationBuilder.AddTenantPolicy("serialisation", "serial_range_member");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "serial_range_member");
            migrationBuilder.DropTable(
                name: "serial_range_member",
                schema: "serialisation");
        }
    }
}
