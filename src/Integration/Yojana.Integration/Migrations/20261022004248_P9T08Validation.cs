using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T08Validation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_validation_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    map_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    target_kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    rehearsal = table.Column<bool>(type: "bit", nullable: false),
                    sample_seed = table.Column<int>(type: "int", nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    failure_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_validation_run_status", "status IN ('queued','running','completed','failed')");
                    table.ForeignKey(
                        name: "fk_migration_validation_run_tenant_id_map_run_id",
                        columns: x => new { x.tenant_id, x.map_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_map_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_signoff_record",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rehearsal = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_signoff_record", x => new { x.tenant_id, x.record_id });
                    table.ForeignKey(
                        name: "fk_migration_signoff_record_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_validation_check",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    limit_kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    source_total = table.Column<long>(type: "bigint", nullable: true),
                    target_total = table.Column<long>(type: "bigint", nullable: true),
                    difference = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    original_result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    explanation = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    how_counted = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    result_fingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ran_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    duration_milliseconds = table.Column<long>(type: "bigint", nullable: false),
                    explained_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    explained_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_check", x => new { x.tenant_id, x.check_id });
                    table.CheckConstraint("ck_migration_validation_check_result", "result IN ('pass','explained','fail','hard_fail','report')");
                    table.ForeignKey(
                        name: "fk_migration_validation_check_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_signoff_line",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    report_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    source_total = table.Column<long>(type: "bigint", nullable: true),
                    target_total = table.Column<long>(type: "bigint", nullable: true),
                    difference = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    explanation = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    preparer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    prepared_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    reviewer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_signoff_line", x => new { x.tenant_id, x.line_id });
                    table.ForeignKey(
                        name: "fk_migration_signoff_line_tenant_id_record_id",
                        columns: x => new { x.tenant_id, x.record_id },
                        principalSchema: "migration",
                        principalTable: "migration_signoff_record",
                        principalColumns: new[] { "tenant_id", "record_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_validation_check_part",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    part_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    value = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_check_part", x => new { x.tenant_id, x.part_id });
                    table.ForeignKey(
                        name: "fk_migration_validation_check_part_tenant_id_check_id",
                        columns: x => new { x.tenant_id, x.check_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_check",
                        principalColumns: new[] { "tenant_id", "check_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_validation_difference",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    difference_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    warehouse_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    source_value = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    target_value = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    difference = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_difference", x => new { x.tenant_id, x.difference_id });
                    table.ForeignKey(
                        name: "fk_migration_validation_difference_tenant_id_check_id",
                        columns: x => new { x.tenant_id, x.check_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_check",
                        principalColumns: new[] { "tenant_id", "check_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_validation_sample",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sample_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    item_family = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    age_band = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_trail_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    target_trail_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    traced_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    traced_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_sample", x => new { x.tenant_id, x.sample_id });
                    table.CheckConstraint("ck_migration_validation_sample_outcome", "outcome IS NULL OR outcome IN ('traced','mismatch')");
                    table.ForeignKey(
                        name: "fk_migration_validation_sample_tenant_id_check_id",
                        columns: x => new { x.tenant_id, x.check_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_check",
                        principalColumns: new[] { "tenant_id", "check_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_validation_signature",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    signature_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    check_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    signed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    superseded = table.Column<bool>(type: "bit", nullable: false),
                    superseded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    carried_from_signature_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_validation_signature", x => new { x.tenant_id, x.signature_id });
                    table.ForeignKey(
                        name: "fk_migration_validation_signature_tenant_id_check_id",
                        columns: x => new { x.tenant_id, x.check_id },
                        principalSchema: "migration",
                        principalTable: "migration_validation_check",
                        principalColumns: new[] { "tenant_id", "check_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_migration_signoff_line_tenant_id_record_id_position",
                schema: "migration",
                table: "migration_signoff_line",
                columns: new[] { "tenant_id", "record_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_migration_signoff_record_tenant_id_run_id",
                schema: "migration",
                table: "migration_signoff_record",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_migration_validation_check_tenant_id_run_id_code",
                schema: "migration",
                table: "migration_validation_check",
                columns: new[] { "tenant_id", "run_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_migration_validation_check_part_tenant_id_check_id_code",
                schema: "migration",
                table: "migration_validation_check_part",
                columns: new[] { "tenant_id", "check_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_difference_tenant_id_check_id_difference_id",
                schema: "migration",
                table: "migration_validation_difference",
                columns: new[] { "tenant_id", "check_id", "difference_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_run_tenant_id_batch_id_requested_at_run_id",
                schema: "migration",
                table: "migration_validation_run",
                columns: new[] { "tenant_id", "batch_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_run_tenant_id_map_run_id_run_id",
                schema: "migration",
                table: "migration_validation_run",
                columns: new[] { "tenant_id", "map_run_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_run_tenant_id_status_requested_at_run_id",
                schema: "migration",
                table: "migration_validation_run",
                columns: new[] { "tenant_id", "status", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_sample_tenant_id_check_id_sample_id",
                schema: "migration",
                table: "migration_validation_sample",
                columns: new[] { "tenant_id", "check_id", "sample_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_sample_tenant_id_run_id_sample_id",
                schema: "migration",
                table: "migration_validation_sample",
                columns: new[] { "tenant_id", "run_id", "sample_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_validation_signature_tenant_id_check_id_role_superseded_signature_id",
                schema: "migration",
                table: "migration_validation_signature",
                columns: new[] { "tenant_id", "check_id", "role", "superseded", "signature_id" });

            foreach (var table in new[]
            {
                "migration_validation_run", "migration_validation_check", "migration_validation_check_part",
                "migration_validation_difference", "migration_validation_sample", "migration_validation_signature",
                "migration_signoff_record", "migration_signoff_line",
            })
                migrationBuilder.AddTenantPolicy("migration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
            {
                "migration_signoff_line", "migration_signoff_record", "migration_validation_signature",
                "migration_validation_sample", "migration_validation_difference", "migration_validation_check_part",
                "migration_validation_check", "migration_validation_run",
            })
                migrationBuilder.RemoveTenantPolicy("migration", table);

            migrationBuilder.DropTable(
                name: "migration_signoff_line",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_check_part",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_difference",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_sample",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_signature",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_signoff_record",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_check",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_validation_run",
                schema: "migration");
        }
    }
}
