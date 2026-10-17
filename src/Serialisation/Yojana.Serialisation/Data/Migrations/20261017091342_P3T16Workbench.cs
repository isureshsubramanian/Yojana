using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T16Workbench : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workbench_request",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    choice = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    command = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    target_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    answers_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    evidence_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    preview_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    preview_hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    initiated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    initiator_roles_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    initiated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    required_approver_roles_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    approvals_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    rejected_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    rejected_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    decision_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    committed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    committed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workbench_request", x => new { x.tenant_id, x.request_id });
                    table.CheckConstraint("ck_workbench_request_answers_json", "ISJSON(answers_json) = 1");
                    table.CheckConstraint("ck_workbench_request_approvals_json", "ISJSON(approvals_json) = 1");
                    table.CheckConstraint("ck_workbench_request_evidence_json", "ISJSON(evidence_json) = 1");
                    table.CheckConstraint("ck_workbench_request_initiator_roles_json", "ISJSON(initiator_roles_json) = 1");
                    table.CheckConstraint("ck_workbench_request_preview_json", "ISJSON(preview_json) = 1");
                    table.CheckConstraint("ck_workbench_request_required_approver_roles_json", "ISJSON(required_approver_roles_json) = 1");
                    table.CheckConstraint("ck_workbench_request_result_json", "result_json IS NULL OR ISJSON(result_json) = 1");
                    table.CheckConstraint("ck_workbench_request_status", "status IN ('draft', 'awaiting_approval', 'approved', 'rejected', 'committed', 'superseded')");
                    table.CheckConstraint("ck_workbench_request_target_json", "ISJSON(target_json) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_workbench_request_tenant_id_status_initiated_at",
                schema: "serialisation",
                table: "workbench_request",
                columns: new[] { "tenant_id", "status", "initiated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_workbench_request_tenant_id_command_id",
                schema: "serialisation",
                table: "workbench_request",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "workbench_request");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "workbench_request");

            migrationBuilder.DropTable(
                name: "workbench_request",
                schema: "serialisation");
        }
    }
}
