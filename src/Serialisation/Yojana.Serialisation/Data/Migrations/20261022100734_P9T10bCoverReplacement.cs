using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T10bCoverReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_cover_replacement",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    closed_entitlements = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "[]"),
                    events_written = table.Column<int>(type: "int", nullable: false),
                    replaced_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_cover_replacement", x => new { x.tenant_id, x.load_id, x.unit_id });
                    table.CheckConstraint("ck_migration_cover_replacement_closed_entitlements_json", "ISJSON(closed_entitlements) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_cover_replacement_tenant_id_load_id_plan_unit_id",
                schema: "serialisation",
                table: "migration_cover_replacement",
                columns: new[] { "tenant_id", "load_id", "plan_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_cover_replacement_tenant_id_unit_id_replaced_at",
                schema: "serialisation",
                table: "migration_cover_replacement",
                columns: new[] { "tenant_id", "unit_id", "replaced_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "migration_cover_replacement");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "migration_cover_replacement");
            migrationBuilder.DropTable(
                name: "migration_cover_replacement",
                schema: "serialisation");
        }
    }
}
