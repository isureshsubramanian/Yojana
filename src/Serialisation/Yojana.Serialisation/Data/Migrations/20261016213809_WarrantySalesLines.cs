using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class WarrantySalesLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warranty_line_exception",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    exception_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_line = table.Column<int>(type: "int", nullable: false),
                    target_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    target_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    missing_input = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    trigger_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    raised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    resolved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranty_line_exception", x => new { x.tenant_id, x.exception_id });
                    table.CheckConstraint("ck_warranty_line_exception_status", "status IN ('open', 'resolved', 'waived')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_line_exception_tenant_id_status_raised_at",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "status", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_line_exception_tenant_id_target_document_type_target_document_id_status",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "target_document_type", "target_document_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_warranty_line_exception_tenant_id_document_type_document_id_document_line",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "document_type", "document_id", "document_line" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "warranty_line_exception");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "warranty_line_exception");

            migrationBuilder.DropTable(
                name: "warranty_line_exception",
                schema: "serialisation");
        }
    }
}
