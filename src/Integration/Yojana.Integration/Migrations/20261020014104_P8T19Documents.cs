using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T19Documents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_document_receipt",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    outbound_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    change_marker = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_document_receipt", x => new { x.tenant_id, x.connection_id, x.external_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_document_receipt_tenant_id_outbound_command_id",
                schema: "integration",
                table: "outbound_document_receipt",
                columns: new[] { "tenant_id", "outbound_command_id" });
            migrationBuilder.AddTenantPolicy("integration", "outbound_document_receipt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "outbound_document_receipt");
            migrationBuilder.DropTable(
                name: "outbound_document_receipt",
                schema: "integration");
        }
    }
}
