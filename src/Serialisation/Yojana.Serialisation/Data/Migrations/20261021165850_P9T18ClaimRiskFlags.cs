using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <summary>P9-T18 immutable claim risk hits and their one-time second-review decisions.</summary>
    public partial class P9T18ClaimRiskFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "claim_risk_flag",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    flag_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_code = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    threshold_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    evidence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    raised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    raised_by_evaluation = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    decision_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_risk_flag", x => new { x.tenant_id, x.flag_id });
                    table.CheckConstraint("ck_claim_risk_flag_decision", "(status = 'open' AND decided_at IS NULL AND decided_by IS NULL AND decision_reason IS NULL) OR (status IN ('cleared', 'confirmed') AND decided_at IS NOT NULL AND decided_by IS NOT NULL AND decision_reason IS NOT NULL)");
                    table.CheckConstraint("ck_claim_risk_flag_evidence_json", "ISJSON(evidence) = 1");
                    table.CheckConstraint("ck_claim_risk_flag_status", "status IN ('open', 'cleared', 'confirmed')");
                    table.CheckConstraint("ck_claim_risk_flag_threshold_snapshot_json", "ISJSON(threshold_snapshot) = 1");
                    table.ForeignKey(
                        name: "fk_claim_risk_flag_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "serialisation",
                        principalTable: "claim",
                        principalColumns: new[] { "tenant_id", "claim_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_risk_flag_tenant_id_rule_code_raised_at_status",
                schema: "serialisation",
                table: "claim_risk_flag",
                columns: new[] { "tenant_id", "rule_code", "raised_at", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_risk_flag_tenant_id_status_raised_at_flag_id",
                schema: "serialisation",
                table: "claim_risk_flag",
                columns: new[] { "tenant_id", "status", "raised_at", "flag_id" });

            migrationBuilder.CreateIndex(
                name: "ux_claim_risk_flag_tenant_id_claim_id_rule_code",
                schema: "serialisation",
                table: "claim_risk_flag",
                columns: new[] { "tenant_id", "claim_id", "rule_code" },
                unique: true);
            migrationBuilder.AddTenantPolicy("serialisation", "claim_risk_flag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "claim_risk_flag");
            migrationBuilder.DropTable(
                name: "claim_risk_flag",
                schema: "serialisation");
        }
    }
}
