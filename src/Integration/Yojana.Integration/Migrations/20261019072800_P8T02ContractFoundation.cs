using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T02ContractFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.CreateTable(
                name: "connection",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    display_label = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    archived_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection", x => new { x.tenant_id, x.connection_id })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("ck_connection_status", "status IN ('active','archived')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_tenant_id_connector_code_connection_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "connector_code", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_tenant_id_status_created_at_connection_id",
                schema: "integration",
                table: "connection",
                columns: new[] { "tenant_id", "status", "created_at", "connection_id" });

            migrationBuilder.AddTenantPolicy("integration", "connection");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connection");
            migrationBuilder.DropTable(
                name: "connection",
                schema: "integration");
        }
    }
}
