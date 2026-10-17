using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Coverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "coverage_decision_ref", schema: "serialisation", table: "rma_line", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "installation_link_id", schema: "serialisation", table: "entitlement", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "installed_parent_unit_id", schema: "serialisation", table: "entitlement", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "service_parent_entitlement_id", schema: "serialisation", table: "entitlement", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "source_event_id", schema: "serialisation", table: "entitlement", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "coverage_decision_ref", schema: "serialisation", table: "claim_line", type: "uniqueidentifier", nullable: true);

            migrationBuilder.CreateTable(
                name: "coverage_decision",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    coverage_decision_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reason = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    work_type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    work_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    work_line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    work_line_key = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    before_decision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    after_decision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coverage_decision", x => new { x.tenant_id, x.coverage_decision_id });
                    table.CheckConstraint("ck_coverage_decision_after_json", "ISJSON(after_decision) = 1");
                    table.CheckConstraint("ck_coverage_decision_before_json", "ISJSON(before_decision) = 1");
                    table.CheckConstraint("ck_coverage_decision_reason", "reason IN ('entitlement_changed')");
                    table.CheckConstraint("ck_coverage_decision_work_type", "work_type IN ('rma', 'claim', 'service_order')");
                });

            migrationBuilder.CreateIndex(name: "ix_entitlement_tenant_id_installed_parent_unit_id_installation_link_id",
                schema: "serialisation", table: "entitlement", columns: new[] { "tenant_id", "installed_parent_unit_id", "installation_link_id" });
            migrationBuilder.CreateIndex(name: "ux_entitlement_tenant_id_source_event_id_service_parent_entitlement_id",
                schema: "serialisation", table: "entitlement", columns: new[] { "tenant_id", "source_event_id", "service_parent_entitlement_id" },
                unique: true, filter: "[source_event_id] IS NOT NULL AND [service_parent_entitlement_id] IS NOT NULL");
            migrationBuilder.CreateIndex(name: "ix_coverage_decision_tenant_id_unit_id_decided_at",
                schema: "serialisation", table: "coverage_decision", columns: new[] { "tenant_id", "unit_id", "decided_at" });
            migrationBuilder.CreateIndex(name: "ix_coverage_decision_tenant_id_work_type_work_id_work_line_id_decided_at",
                schema: "serialisation", table: "coverage_decision", columns: new[] { "tenant_id", "work_type", "work_id", "work_line_id", "decided_at" });
            migrationBuilder.CreateIndex(name: "ux_coverage_decision_tenant_id_source_event_id_work_type_work_id_work_line_key",
                schema: "serialisation", table: "coverage_decision", columns: new[] { "tenant_id", "source_event_id", "work_type", "work_id", "work_line_key" }, unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "coverage_decision");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "coverage_decision");
            migrationBuilder.DropTable(name: "coverage_decision", schema: "serialisation");
            migrationBuilder.DropIndex(name: "ix_entitlement_tenant_id_installed_parent_unit_id_installation_link_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropIndex(name: "ux_entitlement_tenant_id_source_event_id_service_parent_entitlement_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropColumn(name: "coverage_decision_ref", schema: "serialisation", table: "rma_line");
            migrationBuilder.DropColumn(name: "installation_link_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropColumn(name: "installed_parent_unit_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropColumn(name: "service_parent_entitlement_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropColumn(name: "source_event_id", schema: "serialisation", table: "entitlement");
            migrationBuilder.DropColumn(name: "coverage_decision_ref", schema: "serialisation", table: "claim_line");
        }
    }
}
