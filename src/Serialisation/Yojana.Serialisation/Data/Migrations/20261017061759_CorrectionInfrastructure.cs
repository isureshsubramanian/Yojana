using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class CorrectionInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "correction_record",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    reason_code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    root_cause_category = table.Column<int>(type: "int", nullable: false),
                    initiated_via = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    initiating_role = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    corrects_correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    unit_count = table.Column<int>(type: "int", nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    evidence_refs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    approvers = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction_record", x => new { x.tenant_id, x.correction_id });
                    table.CheckConstraint("ck_correction_record_approvers_json", "ISJSON(approvers) = 1");
                    table.CheckConstraint("ck_correction_record_evidence_refs_json", "ISJSON(evidence_refs) = 1");
                    table.CheckConstraint("ck_correction_record_root_cause", "root_cause_category BETWEEN 1 AND 7");
                });

            migrationBuilder.CreateIndex(
                name: "ix_correction_record_tenant_id_corrects_correction_id",
                schema: "serialisation",
                table: "correction_record",
                columns: new[] { "tenant_id", "corrects_correction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_correction_record_tenant_id_event_time_root_cause_category_initiating_role_initiated_via",
                schema: "serialisation",
                table: "correction_record",
                columns: new[] { "tenant_id", "event_time", "root_cause_category", "initiating_role", "initiated_via" });

            migrationBuilder.AddTenantPolicy("serialisation", "correction_record");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "correction_record");

            migrationBuilder.DropTable(
                name: "correction_record",
                schema: "serialisation");
        }
    }
}
