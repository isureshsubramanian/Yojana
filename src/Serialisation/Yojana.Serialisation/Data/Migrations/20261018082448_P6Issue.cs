using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Issue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_run",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_run", x => new { x.tenant_id, x.command_id });
                    table.ForeignKey(
                        name: "fk_certificate_run_tenant_id_selection_id",
                        columns: x => new { x.tenant_id, x.selection_id },
                        principalSchema: "serialisation",
                        principalTable: "selection_set",
                        principalColumns: new[] { "tenant_id", "selection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "certificate_run_unit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serial = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    item_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    revision = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    certificate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    certificate_version = table.Column<int>(type: "int", nullable: true),
                    tracking_number = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    outcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    rule = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    unblocking_step = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    copy = table.Column<bool>(type: "bit", nullable: false),
                    processed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_certificate_run_unit", x => new { x.tenant_id, x.command_id, x.unit_id });
                    table.CheckConstraint("ck_certificate_run_unit_outcome", "outcome IN ('issued', 'copied', 'skipped', 'failed')");
                    table.ForeignKey(
                        name: "fk_certificate_run_unit_tenant_id_certificate_id",
                        columns: x => new { x.tenant_id, x.certificate_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_instance",
                        principalColumns: new[] { "tenant_id", "certificate_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_certificate_run_unit_tenant_id_command_id",
                        columns: x => new { x.tenant_id, x.command_id },
                        principalSchema: "serialisation",
                        principalTable: "certificate_run",
                        principalColumns: new[] { "tenant_id", "command_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_run_tenant_id_selection_id_created_at",
                schema: "serialisation",
                table: "certificate_run",
                columns: new[] { "tenant_id", "selection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_run_unit_tenant_id_certificate_id",
                schema: "serialisation",
                table: "certificate_run_unit",
                columns: new[] { "tenant_id", "certificate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_run_unit_tenant_id_command_id_outcome",
                schema: "serialisation",
                table: "certificate_run_unit",
                columns: new[] { "tenant_id", "command_id", "outcome" });

            migrationBuilder.AddTenantPolicy("serialisation", "certificate_run");
            migrationBuilder.AddTenantPolicy("serialisation", "certificate_run_unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_run_unit");
            migrationBuilder.RemoveTenantPolicy("serialisation", "certificate_run");

            migrationBuilder.DropTable(
                name: "certificate_run_unit",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "certificate_run",
                schema: "serialisation");
        }
    }
}
