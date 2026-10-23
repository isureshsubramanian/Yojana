using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T10cErpChangeNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_change_notice",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_notice_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erp_change_run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    step = table.Column<int>(type: "int", nullable: false),
                    recipient_role = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    occurrence_key = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    recipient_count = table.Column<int>(type: "int", nullable: false),
                    counts_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    link_path = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    sent_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_change_notice", x => new { x.tenant_id, x.erp_change_notice_id });
                    table.CheckConstraint("ck_erp_change_notice_kind", "kind IN ('step_open','morning_digest')");
                    table.ForeignKey(
                        name: "fk_erp_change_notice_tenant_id_erp_change_run_id",
                        columns: x => new { x.tenant_id, x.erp_change_run_id },
                        principalSchema: "integration",
                        principalTable: "erp_change_run",
                        principalColumns: new[] { "tenant_id", "erp_change_run_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erp_change_notice_tenant_id_sent_at_erp_change_notice_id",
                schema: "integration",
                table: "erp_change_notice",
                columns: new[] { "tenant_id", "sent_at", "erp_change_notice_id" });

            migrationBuilder.CreateIndex(
                name: "ux_erp_change_notice_tenant_id_erp_change_run_id_kind_step_recipient_role_occurrence_key",
                schema: "integration",
                table: "erp_change_notice",
                columns: new[] { "tenant_id", "erp_change_run_id", "kind", "step", "recipient_role", "occurrence_key" },
                unique: true);

            migrationBuilder.AddTenantPolicy("integration", "erp_change_notice");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "erp_change_notice");

            migrationBuilder.DropTable(
                name: "erp_change_notice",
                schema: "integration");
        }
    }
}
