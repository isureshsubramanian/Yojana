using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6RmaVolumeReadPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_rma_tenant_id_stage_requested_at",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.AddColumn<bool>(
                name: "is_exception",
                schema: "serialisation",
                table: "rma_line",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "exception_count",
                schema: "serialisation",
                table: "rma",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "has_exception",
                schema: "serialisation",
                table: "rma",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "rma_stage_counter",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stage = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    slot = table.Column<byte>(type: "tinyint", nullable: false),
                    document_count = table.Column<int>(type: "int", nullable: false),
                    exception_count = table.Column<int>(type: "int", nullable: false),
                    last_rebuild_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    last_rebuilt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_rebuilt_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rma_stage_counter", x => new { x.tenant_id, x.stage, x.slot });
                    table.CheckConstraint("ck_rma_stage_counter_counts", "document_count >= 0 AND exception_count >= 0 AND exception_count <= document_count");
                    table.CheckConstraint("ck_rma_stage_counter_slot", "slot BETWEEN 0 AND 15");
                    table.CheckConstraint("ck_rma_stage_counter_stage", "stage IN ('requested', 'authorised', 'in_transit', 'received', 'dispositioned', 'closed')");
                });

            migrationBuilder.AddTenantPolicy("serialisation", "rma_stage_counter");

            // This is a cross-tenant projection backfill. RLS is off only for these statements and is restored before
            // indexes and constraints are exposed to the application.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                UPDATE serialisation.rma_line
                SET is_exception = CAST(1 AS bit)
                WHERE provisional_unit_id IS NOT NULL
                   OR receipt_outcome IN ('mismatch', 'unauthorised', 'counterfeit');

                UPDATE r
                SET exception_count = exceptions.exception_count,
                    has_exception = CASE WHEN exceptions.exception_count > 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
                FROM serialisation.rma r
                CROSS APPLY (
                    SELECT COUNT(*) AS exception_count
                    FROM serialisation.rma_line line
                    WHERE line.tenant_id = r.tenant_id AND line.rma_id = r.rma_id
                      AND line.is_exception = CAST(1 AS bit)
                ) exceptions;

                WITH rma_slots AS (
                    SELECT r.tenant_id, r.rma_id, r.stage, r.has_exception,
                           CONVERT(tinyint, SUBSTRING(HASHBYTES('SHA2_256',
                               CONVERT(varchar(32), LOWER(REPLACE(CONVERT(char(36), r.rma_id), '-', '')))), 1, 1)) % 16 AS slot
                    FROM serialisation.rma r
                )
                INSERT INTO serialisation.rma_stage_counter
                    (tenant_id, stage, slot, document_count, exception_count)
                SELECT tenants.tenant_id, stages.stage, slots.slot,
                       COUNT(r.rma_id),
                       COALESCE(SUM(CASE WHEN r.has_exception = CAST(1 AS bit) THEN 1 ELSE 0 END), 0)
                FROM (SELECT DISTINCT tenant_id FROM serialisation.rma) tenants
                CROSS JOIN (VALUES ('requested'), ('authorised'), ('in_transit'), ('received'), ('dispositioned'), ('closed')) stages(stage)
                CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6), (7),
                                   (8), (9), (10), (11), (12), (13), (14), (15)) slots(slot)
                LEFT JOIN rma_slots r ON r.tenant_id = tenants.tenant_id AND r.stage = stages.stage AND r.slot = slots.slot
                GROUP BY tenants.tenant_id, stages.stage, slots.slot;
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.CreateIndex(
                name: "ix_rma_line_tenant_id_is_exception_rma_id_sequence",
                schema: "serialisation",
                table: "rma_line",
                columns: new[] { "tenant_id", "is_exception", "rma_id", "sequence" },
                filter: "[is_exception] = CAST(1 AS bit)")
                .Annotation("SqlServer:Include", new[] { "provisional_unit_id", "receipt_outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_has_exception_requested_at_rma_id",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "has_exception", "requested_at", "rma_id" },
                descending: new[] { false, true, false, false })
                .Annotation("SqlServer:Include", new[] { "rma_number", "stage", "request_date", "returning_party_ref", "closed_at", "dispositioned_at", "received_at", "in_transit_at", "authorised_at", "requested_by", "last_actor_ref", "exception_count" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_stage_has_exception_requested_at_rma_id",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "stage", "has_exception", "requested_at", "rma_id" },
                descending: new[] { false, false, true, false, false })
                .Annotation("SqlServer:Include", new[] { "rma_number", "request_date", "returning_party_ref", "closed_at", "dispositioned_at", "received_at", "in_transit_at", "authorised_at", "requested_by", "last_actor_ref", "exception_count" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_rma_exception_count",
                schema: "serialisation",
                table: "rma",
                sql: "exception_count >= 0 AND has_exception = CASE WHEN exception_count > 0 THEN 1 ELSE 0 END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "rma_stage_counter");

            migrationBuilder.DropTable(
                name: "rma_stage_counter",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_rma_line_tenant_id_is_exception_rma_id_sequence",
                schema: "serialisation",
                table: "rma_line");

            migrationBuilder.DropIndex(
                name: "ix_rma_tenant_id_has_exception_requested_at_rma_id",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropIndex(
                name: "ix_rma_tenant_id_stage_has_exception_requested_at_rma_id",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rma_exception_count",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropColumn(
                name: "is_exception",
                schema: "serialisation",
                table: "rma_line");

            migrationBuilder.DropColumn(
                name: "exception_count",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropColumn(
                name: "has_exception",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_stage_requested_at",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "stage", "requested_at" });
        }
    }
}
