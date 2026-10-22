using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T10MigrationLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_load_unit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    load_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_count = table.Column<int>(type: "int", nullable: false),
                    applied_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_load_unit", x => new { x.tenant_id, x.load_id, x.plan_unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ux_migration_load_unit_tenant_id_unit_id",
                schema: "serialisation",
                table: "migration_load_unit",
                columns: new[] { "tenant_id", "unit_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "migration_load_unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "migration_load_unit");
            migrationBuilder.DropTable(
                name: "migration_load_unit",
                schema: "serialisation");
        }
    }
}
