using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T10PortalWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portal_registration",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    registration_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    evidence_kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    delivery_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    evidence_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    evidence_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    attachment_file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    attachment_content_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    attachment_content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    grace_days = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    submitted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    submitted_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    approval_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    rebase_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portal_registration", x => new { x.tenant_id, x.registration_id });
                    table.CheckConstraint("ck_portal_registration_evidence_kind", "evidence_kind IN ('delivery', 'installation')");
                    table.CheckConstraint("ck_portal_registration_status", "status IN ('recorded', 'pending_review', 'approved')");
                    table.ForeignKey(
                        name: "fk_portal_registration_tenant_id_entitlement_id",
                        columns: x => new { x.tenant_id, x.entitlement_id },
                        principalSchema: "serialisation",
                        principalTable: "entitlement",
                        principalColumns: new[] { "tenant_id", "entitlement_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_portal_registration_tenant_id_unit_id",
                        columns: x => new { x.tenant_id, x.unit_id },
                        principalSchema: "serialisation",
                        principalTable: "unit",
                        principalColumns: new[] { "tenant_id", "unit_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_portal_registration_tenant_id_entitlement_id",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "entitlement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_registration_tenant_id_party_ref_submitted_at",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "party_ref", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_registration_tenant_id_status_submitted_at_registration_id",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "status", "submitted_at", "registration_id" });

            migrationBuilder.CreateIndex(
                name: "ix_portal_registration_tenant_id_unit_id_submitted_at",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "unit_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_portal_registration_tenant_id_approval_command_id",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "approval_command_id" },
                unique: true,
                filter: "[approval_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_portal_registration_tenant_id_command_id",
                schema: "serialisation",
                table: "portal_registration",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "portal_registration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portal_registration",
                schema: "serialisation");
        }
    }
}
