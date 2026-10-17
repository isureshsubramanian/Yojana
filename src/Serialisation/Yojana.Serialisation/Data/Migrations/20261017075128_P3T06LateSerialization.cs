using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T06LateSerialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_tracking_mode_change",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    change_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    previous_tracking_mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    tracking_mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    effective_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    corrects_correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    commissioned_count = table.Column<int>(type: "int", nullable: false),
                    exempted_count = table.Column<int>(type: "int", nullable: false),
                    kits_captured = table.Column<int>(type: "int", nullable: false),
                    stock_coverage = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_tracking_mode_change", x => new { x.tenant_id, x.change_id });
                    table.CheckConstraint("ck_item_tracking_mode_change_stock_coverage_json", "ISJSON(stock_coverage) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_tracking_mode_change_tenant_id_item_id_effective_at",
                schema: "serialisation",
                table: "item_tracking_mode_change",
                columns: new[] { "tenant_id", "item_id", "effective_at" });

            migrationBuilder.CreateIndex(
                name: "ux_item_tracking_mode_change_tenant_id_correction_id",
                schema: "serialisation",
                table: "item_tracking_mode_change",
                columns: new[] { "tenant_id", "correction_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "item_tracking_mode_change");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "item_tracking_mode_change");

            migrationBuilder.DropTable(
                name: "item_tracking_mode_change",
                schema: "serialisation");
        }
    }
}
