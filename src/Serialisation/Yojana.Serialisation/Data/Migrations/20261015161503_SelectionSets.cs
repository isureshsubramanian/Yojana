using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class SelectionSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "selection_set",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    source_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    resolved_count = table.Column<int>(type: "int", nullable: false),
                    unknown_count = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    executed_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    executed_operation = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    executed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    source_document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    source_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    approver_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_selection_set", x => new { x.tenant_id, x.selection_id });
                    table.CheckConstraint("ck_selection_set_source_json", "ISJSON(source_json) = 1");
                });

            migrationBuilder.CreateTable(
                name: "selection_set_member",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serial = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_selection_set_member", x => new { x.tenant_id, x.selection_id, x.unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_selection_set_tenant_id_created_at",
                schema: "serialisation",
                table: "selection_set",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_selection_set_tenant_id_executed_command_id",
                schema: "serialisation",
                table: "selection_set",
                columns: new[] { "tenant_id", "executed_command_id" });

            migrationBuilder.CreateIndex(
                name: "ix_selection_set_member_tenant_id_selection_id_serial",
                schema: "serialisation",
                table: "selection_set_member",
                columns: new[] { "tenant_id", "selection_id", "serial" });

            migrationBuilder.AddTenantPolicy("serialisation", "selection_set");
            migrationBuilder.AddTenantPolicy("serialisation", "selection_set_member");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "selection_set_member");
            migrationBuilder.RemoveTenantPolicy("serialisation", "selection_set");
            migrationBuilder.DropTable(
                name: "selection_set",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "selection_set_member",
                schema: "serialisation");
        }
    }
}
