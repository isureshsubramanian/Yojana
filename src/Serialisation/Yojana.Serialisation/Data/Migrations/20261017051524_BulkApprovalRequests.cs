using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class BulkApprovalRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_approval_request",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    arguments_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    fingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    total = table.Column<int>(type: "int", nullable: false),
                    change_count = table.Column<int>(type: "int", nullable: false),
                    skip_count = table.Column<int>(type: "int", nullable: false),
                    block_count = table.Column<int>(type: "int", nullable: false),
                    rules = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    requested_roles = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    decided_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    decided_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    decision_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    executed_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_approval_request", x => new { x.tenant_id, x.request_id });
                    table.CheckConstraint("ck_bulk_approval_request_arguments_json", "ISJSON(arguments_json) = 1");
                    table.CheckConstraint("ck_bulk_approval_request_status", "status IN ('pending', 'approved', 'rejected', 'withdrawn', 'stale')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_approval_request_tenant_id_status_requested_at",
                schema: "serialisation",
                table: "bulk_approval_request",
                columns: new[] { "tenant_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_bulk_approval_request_tenant_id_command_id",
                schema: "serialisation",
                table: "bulk_approval_request",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bulk_approval_request_tenant_id_selection_id_operation",
                schema: "serialisation",
                table: "bulk_approval_request",
                columns: new[] { "tenant_id", "selection_id", "operation" },
                unique: true,
                filter: "[status] = 'pending'");

            migrationBuilder.AddTenantPolicy("serialisation", "bulk_approval_request");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "bulk_approval_request");

            migrationBuilder.DropTable(
                name: "bulk_approval_request",
                schema: "serialisation");
        }
    }
}
