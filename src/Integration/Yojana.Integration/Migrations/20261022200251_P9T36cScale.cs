using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T36cScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_unit_candidate_source");

            migrationBuilder.DropIndex(
                name: "ix_migration_plan_event_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_event");

            migrationBuilder.CreateTable(
                name: "migration_map_waiting_candidate",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_map_waiting_candidate", x => new { x.tenant_id, x.map_run_id, x.candidate_id });
                    table.ForeignKey(
                        name: "fk_migration_map_waiting_candidate_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_map_waiting_candidate_tenant_id_map_run_id",
                        columns: x => new { x.tenant_id, x.map_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_map_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_candidate_id_source_kind_role",
                schema: "migration",
                table: "migration_unit_candidate_source",
                columns: new[] { "tenant_id", "candidate_id", "source_kind", "role" })
                .Annotation("SqlServer:Include", new[] { "source_key" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_unit_tenant_id_map_run_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_unit",
                columns: new[] { "tenant_id", "map_run_id", "plan_unit_id" })
                .Annotation("SqlServer:Include", new[] { "candidate_id", "condition_grade" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_event_tenant_id_plan_unit_id_sequence",
                schema: "migration",
                table: "migration_plan_event",
                columns: new[] { "tenant_id", "plan_unit_id", "sequence" })
                .Annotation("SqlServer:Include", new[] { "movement_kind" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_log_entry",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "batch_id", "source_system", "log_entry" })
                .Annotation("SqlServer:Include", new[] { "absent_since_batch_id", "stock_effect" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_map_waiting_candidate_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_map_waiting_candidate",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_map_waiting_candidate_tenant_id_map_run_id_source_key",
                schema: "migration",
                table: "migration_map_waiting_candidate",
                columns: new[] { "tenant_id", "map_run_id", "source_key" });

            migrationBuilder.AddTenantPolicy("migration", "migration_map_waiting_candidate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_map_waiting_candidate");

            migrationBuilder.DropTable(
                name: "migration_map_waiting_candidate",
                schema: "migration");

            migrationBuilder.DropIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_candidate_id_source_kind_role",
                schema: "migration",
                table: "migration_unit_candidate_source");

            migrationBuilder.DropIndex(
                name: "ix_migration_plan_unit_tenant_id_map_run_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_unit");

            migrationBuilder.DropIndex(
                name: "ix_migration_plan_event_tenant_id_plan_unit_id_sequence",
                schema: "migration",
                table: "migration_plan_event");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_log_entry",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.CreateIndex(
                name: "ix_migration_unit_candidate_source_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_unit_candidate_source",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_plan_event_tenant_id_plan_unit_id",
                schema: "migration",
                table: "migration_plan_event",
                columns: new[] { "tenant_id", "plan_unit_id" });
        }
    }
}
