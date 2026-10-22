using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T10Apply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_apply_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rehearsal = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    failure_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    cursor_plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    units_in_plan = table.Column<long>(type: "bigint", nullable: false),
                    applied = table.Column<long>(type: "bigint", nullable: false),
                    already_applied = table.Column<long>(type: "bigint", nullable: false),
                    refused = table.Column<long>(type: "bigint", nullable: false),
                    events_written = table.Column<long>(type: "bigint", nullable: false),
                    rolled_back_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    rolled_back_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_apply_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_apply_run_status", "status IN ('queued','running','completed','failed','rolled_back')");
                    table.ForeignKey(
                        name: "fk_migration_apply_run_tenant_id_map_run_id",
                        columns: x => new { x.tenant_id, x.map_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_map_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_apply_refusal",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    refusal_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reasons = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_apply_refusal", x => new { x.tenant_id, x.refusal_id });
                    table.ForeignKey(
                        name: "fk_migration_apply_refusal_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_apply_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_apply_refusal_tenant_id_run_id_refusal_id",
                schema: "migration",
                table: "migration_apply_refusal",
                columns: new[] { "tenant_id", "run_id", "refusal_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_apply_refusal_tenant_id_run_id_plan_unit_id",
                schema: "migration",
                table: "migration_apply_refusal",
                columns: new[] { "tenant_id", "run_id", "plan_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_apply_run_tenant_id_batch_id_requested_at_run_id",
                schema: "migration",
                table: "migration_apply_run",
                columns: new[] { "tenant_id", "batch_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_apply_run_tenant_id_map_run_id",
                schema: "migration",
                table: "migration_apply_run",
                columns: new[] { "tenant_id", "map_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_apply_run_tenant_id_status_requested_at_run_id",
                schema: "migration",
                table: "migration_apply_run",
                columns: new[] { "tenant_id", "status", "requested_at", "run_id" });

            foreach (var table in new[] { "migration_apply_run", "migration_apply_refusal" })
                migrationBuilder.AddTenantPolicy("migration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "migration_apply_refusal", "migration_apply_run" })
                migrationBuilder.RemoveTenantPolicy("migration", table);

            migrationBuilder.DropTable(
                name: "migration_apply_refusal",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_apply_run",
                schema: "migration");
        }
    }
}
