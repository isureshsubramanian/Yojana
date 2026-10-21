using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T04Profiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_profile_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_profile_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_profile_run_status", "status IN ('queued','running','completed','blocked','failed')");
                    table.ForeignKey(
                        name: "fk_migration_profile_run_tenant_id_batch_id",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalSchema: "migration",
                        principalTable: "migration_batch",
                        principalColumns: new[] { "tenant_id", "batch_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_profile_result",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    class_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    row_count = table.Column<long>(type: "bigint", nullable: true),
                    previous_row_count = table.Column<long>(type: "bigint", nullable: true),
                    change = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    samples_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_profile_result", x => new { x.tenant_id, x.run_id, x.class_code });
                    table.CheckConstraint("ck_migration_profile_result_outcome", "outcome IN ('stop_load','needs_person','fixed_by_rule','loaded_with_note')");
                    table.ForeignKey(
                        name: "fk_migration_profile_result_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_profile_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_profile_result_tenant_id_run_id_class_code",
                schema: "migration",
                table: "migration_profile_result",
                columns: new[] { "tenant_id", "run_id", "class_code" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_profile_run_tenant_id_batch_id_started_at_run_id",
                schema: "migration",
                table: "migration_profile_run",
                columns: new[] { "tenant_id", "batch_id", "started_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_profile_run_tenant_id_status_run_id",
                schema: "migration",
                table: "migration_profile_run",
                columns: new[] { "tenant_id", "status", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_document_type_log_entry",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "batch_id", "source_system", "document_type", "log_entry" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_system_item_code_system_number_log_entry",
                schema: "migration",
                table: "stg_src_itl1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "item_code", "system_number", "log_entry" });

            migrationBuilder.AddTenantPolicy("migration", "migration_profile_run");
            migrationBuilder.AddTenantPolicy("migration", "migration_profile_result");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_profile_result");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_profile_run");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_document_type_log_entry",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_system_item_code_system_number_log_entry",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropTable(
                name: "migration_profile_result",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_profile_run",
                schema: "migration");
        }
    }
}
