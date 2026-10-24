using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Erase01ErasureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erasure_request",
                schema: "platform",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erasure_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subject_kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_reference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    requested_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    failed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    retention_basis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    review_decisions_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    failure_rule = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    failure_detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_request", x => new { x.tenant_id, x.erasure_id });
                    table.CheckConstraint("ck_erasure_request_status", "status IN ('pending','running','completed','failed')");
                    table.CheckConstraint("ck_erasure_request_subject_kind", "subject_kind IN ('Party','User')");
                });

            migrationBuilder.CreateTable(
                name: "erasure_store_count",
                schema: "platform",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    erasure_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    preview_count = table.Column<int>(type: "int", nullable: false),
                    erased_count = table.Column<int>(type: "int", nullable: false),
                    action = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_store_count", x => new { x.tenant_id, x.erasure_id, x.store, x.kind });
                    table.CheckConstraint("ck_erasure_store_count_status", "status IN ('previewed','completed')");
                    table.ForeignKey(
                        name: "fk_erasure_store_count_tenant_id_erasure_id",
                        columns: x => new { x.tenant_id, x.erasure_id },
                        principalSchema: "platform",
                        principalTable: "erasure_request",
                        principalColumns: new[] { "tenant_id", "erasure_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_request_tenant_id_requested_at_erasure_id",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "requested_at", "erasure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_request_tenant_id_subject_kind_subject_id_status",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "subject_kind", "subject_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_erasure_request_tenant_id_request_reference",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "request_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_erasure_store_count_tenant_id_erasure_id_status",
                schema: "platform",
                table: "erasure_store_count",
                columns: new[] { "tenant_id", "erasure_id", "status" });

            migrationBuilder.AddTenantPolicy("platform", "erasure_request");
            migrationBuilder.AddTenantPolicy("platform", "erasure_store_count");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erasure_store_count",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "erasure_request",
                schema: "platform");
        }
    }
}
