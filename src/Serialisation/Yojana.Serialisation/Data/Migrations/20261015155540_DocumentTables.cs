using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "finance_period",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    period = table.Column<string>(type: "char(7)", unicode: false, fixedLength: true, maxLength: 7, nullable: false),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finance_period", x => new { x.tenant_id, x.period });
                });

            migrationBuilder.CreateTable(
                name: "pending_shipment_line",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    pending_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_line = table.Column<int>(type: "int", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    customer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ship_to_location_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    carrier_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    posted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    resolved_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    resolved_count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_shipment_line", x => new { x.tenant_id, x.pending_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_shipment_line_tenant_id_resolved_at",
                schema: "serialisation",
                table: "pending_shipment_line",
                columns: new[] { "tenant_id", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "ux_pending_shipment_line_tenant_id_document_type_document_id_document_line",
                schema: "serialisation",
                table: "pending_shipment_line",
                columns: new[] { "tenant_id", "document_type", "document_id", "document_line" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "pending_shipment_line");
            migrationBuilder.AddTenantPolicy("serialisation", "finance_period");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "finance_period");
            migrationBuilder.RemoveTenantPolicy("serialisation", "pending_shipment_line");
            migrationBuilder.DropTable(
                name: "finance_period",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "pending_shipment_line",
                schema: "serialisation");
        }
    }
}
