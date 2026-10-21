using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T06Reconstruction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_review_item_kind",
                schema: "migration",
                table: "migration_review_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_review_decision_kind",
                schema: "migration",
                table: "migration_review_decision");

            migrationBuilder.AddColumn<string>(
                name: "history_depth",
                schema: "migration",
                table: "migration_unit_candidate",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "migration_reconstruction_run",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    canonicalisation_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    failure_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    candidates_reconstructed = table.Column<long>(type: "bigint", nullable: false),
                    candidates_skipped_by_disposition = table.Column<long>(type: "bigint", nullable: false),
                    cancellation_pairs_removed = table.Column<long>(type: "bigint", nullable: false),
                    candidates_queued_for_review = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_reconstruction_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_migration_reconstruction_run_status", "status IN ('queued','running','completed','failed')");
                    table.ForeignKey(
                        name: "fk_migration_reconstruction_run_tenant_id_canonicalisation_run_id",
                        columns: x => new { x.tenant_id, x.canonicalisation_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_canon_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_history_event",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    history_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconstruction_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    movement_kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    effective_date = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    source_document_type_label = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    document_number = table.Column<int>(type: "int", nullable: true),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    warehouse = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    provenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    event_candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_history_event", x => new { x.tenant_id, x.history_event_id });
                    table.CheckConstraint("ck_migration_history_event_kind", "movement_kind IN ('opening_balance','received','produced','consumed','shipped','customer_returned','vendor_returned','transferred','issued_out','unknown')");
                    table.CheckConstraint("ck_migration_history_event_provenance", "provenance IN ('source','reconstructed')");
                    table.ForeignKey(
                        name: "fk_migration_history_event_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_history_event_tenant_id_event_candidate_id",
                        columns: x => new { x.tenant_id, x.event_candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_event_candidate",
                        principalColumns: new[] { "tenant_id", "event_candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_history_event_tenant_id_reconstruction_run_id",
                        columns: x => new { x.tenant_id, x.reconstruction_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_reconstruction_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_reconstruction_log",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    log_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconstruction_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    related_source_key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    reason_code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_reconstruction_log", x => new { x.tenant_id, x.log_id });
                    table.ForeignKey(
                        name: "fk_migration_reconstruction_log_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_reconstruction_log_tenant_id_reconstruction_run_id",
                        columns: x => new { x.tenant_id, x.reconstruction_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_reconstruction_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "migration_ownership_episode",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ownership_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reconstruction_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    from_history_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    to_history_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    open_reason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    close_reason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_ownership_episode", x => new { x.tenant_id, x.ownership_episode_id });
                    table.ForeignKey(
                        name: "fk_migration_ownership_episode_tenant_id_candidate_id",
                        columns: x => new { x.tenant_id, x.candidate_id },
                        principalSchema: "migration",
                        principalTable: "migration_unit_candidate",
                        principalColumns: new[] { "tenant_id", "candidate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_ownership_episode_tenant_id_from_history_event_id",
                        columns: x => new { x.tenant_id, x.from_history_event_id },
                        principalSchema: "migration",
                        principalTable: "migration_history_event",
                        principalColumns: new[] { "tenant_id", "history_event_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_migration_ownership_episode_tenant_id_reconstruction_run_id",
                        columns: x => new { x.tenant_id, x.reconstruction_run_id },
                        principalSchema: "migration",
                        principalTable: "migration_reconstruction_run",
                        principalColumns: new[] { "tenant_id", "run_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_migration_ownership_episode_tenant_id_to_history_event_id",
                        columns: x => new { x.tenant_id, x.to_history_event_id },
                        principalSchema: "migration",
                        principalTable: "migration_history_event",
                        principalColumns: new[] { "tenant_id", "history_event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_unit_candidate_history_depth",
                schema: "migration",
                table: "migration_unit_candidate",
                sql: "history_depth IS NULL OR history_depth IN ('full','partial','opening_balance_only')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_review_item_kind",
                schema: "migration",
                table: "migration_review_item",
                sql: "kind IN ('same_serial_one_item','same_serial_different_items','orphan_equipment_card','serial_in_user_field','history_needs_review')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_review_decision_kind",
                schema: "migration",
                table: "migration_review_decision",
                sql: "decision IN ('merge_into','keep_apart','fix_in_incumbent','accept_as_recorded','exclude_from_history')");

            migrationBuilder.CreateIndex(
                name: "ix_migration_history_event_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_history_event",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_history_event_tenant_id_event_candidate_id",
                schema: "migration",
                table: "migration_history_event",
                columns: new[] { "tenant_id", "event_candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_history_event_tenant_id_reconstruction_run_id_movement_kind",
                schema: "migration",
                table: "migration_history_event",
                columns: new[] { "tenant_id", "reconstruction_run_id", "movement_kind" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_history_event_tenant_id_reconstruction_run_id_candidate_id_sequence",
                schema: "migration",
                table: "migration_history_event",
                columns: new[] { "tenant_id", "reconstruction_run_id", "candidate_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_ownership_episode_tenant_id_candidate_id_to_history_event_id",
                schema: "migration",
                table: "migration_ownership_episode",
                columns: new[] { "tenant_id", "candidate_id", "to_history_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_ownership_episode_tenant_id_from_history_event_id",
                schema: "migration",
                table: "migration_ownership_episode",
                columns: new[] { "tenant_id", "from_history_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_ownership_episode_tenant_id_reconstruction_run_id_candidate_id_from_history_event_id",
                schema: "migration",
                table: "migration_ownership_episode",
                columns: new[] { "tenant_id", "reconstruction_run_id", "candidate_id", "from_history_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_ownership_episode_tenant_id_to_history_event_id",
                schema: "migration",
                table: "migration_ownership_episode",
                columns: new[] { "tenant_id", "to_history_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_log_tenant_id_candidate_id",
                schema: "migration",
                table: "migration_reconstruction_log",
                columns: new[] { "tenant_id", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_log_tenant_id_reconstruction_run_id_candidate_id_log_id",
                schema: "migration",
                table: "migration_reconstruction_log",
                columns: new[] { "tenant_id", "reconstruction_run_id", "candidate_id", "log_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_log_tenant_id_reconstruction_run_id_reason_code_candidate_id",
                schema: "migration",
                table: "migration_reconstruction_log",
                columns: new[] { "tenant_id", "reconstruction_run_id", "reason_code", "candidate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_run_tenant_id_batch_id_requested_at_run_id",
                schema: "migration",
                table: "migration_reconstruction_run",
                columns: new[] { "tenant_id", "batch_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_run_tenant_id_canonicalisation_run_id_requested_at_run_id",
                schema: "migration",
                table: "migration_reconstruction_run",
                columns: new[] { "tenant_id", "canonicalisation_run_id", "requested_at", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_reconstruction_run_tenant_id_status_requested_at_run_id",
                schema: "migration",
                table: "migration_reconstruction_run",
                columns: new[] { "tenant_id", "status", "requested_at", "run_id" });

            migrationBuilder.AddTenantPolicy("migration", "migration_reconstruction_run");
            migrationBuilder.AddTenantPolicy("migration", "migration_history_event");
            migrationBuilder.AddTenantPolicy("migration", "migration_ownership_episode");
            migrationBuilder.AddTenantPolicy("migration", "migration_reconstruction_log");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("migration", "migration_reconstruction_log");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_ownership_episode");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_history_event");
            migrationBuilder.RemoveTenantPolicy("migration", "migration_reconstruction_run");

            migrationBuilder.DropTable(
                name: "migration_ownership_episode",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_reconstruction_log",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_history_event",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_reconstruction_run",
                schema: "migration");

            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_unit_candidate_history_depth",
                schema: "migration",
                table: "migration_unit_candidate");

            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_review_item_kind",
                schema: "migration",
                table: "migration_review_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_review_decision_kind",
                schema: "migration",
                table: "migration_review_decision");

            migrationBuilder.DropColumn(
                name: "history_depth",
                schema: "migration",
                table: "migration_unit_candidate");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_review_item_kind",
                schema: "migration",
                table: "migration_review_item",
                sql: "kind IN ('same_serial_one_item','same_serial_different_items','orphan_equipment_card','serial_in_user_field')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_review_decision_kind",
                schema: "migration",
                table: "migration_review_decision",
                sql: "decision IN ('merge_into','keep_apart','fix_in_incumbent')");
        }
    }
}
