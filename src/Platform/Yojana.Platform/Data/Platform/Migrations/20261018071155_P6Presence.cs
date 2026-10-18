using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P6Presence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_presence",
                schema: "platform",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    document_id = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    since = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_heartbeat = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_presence", x => new { x.tenant_id, x.user_id, x.document_type, x.document_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_presence_tenant_id_document_type_document_id_last_heartbeat",
                schema: "platform",
                table: "document_presence",
                columns: new[] { "tenant_id", "document_type", "document_id", "last_heartbeat" });

            migrationBuilder.CreateIndex(
                name: "ix_document_presence_tenant_id_last_heartbeat",
                schema: "platform",
                table: "document_presence",
                columns: new[] { "tenant_id", "last_heartbeat" });

            migrationBuilder.AddTenantPolicy("platform", "document_presence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("platform", "document_presence");
            migrationBuilder.DropTable(
                name: "document_presence",
                schema: "platform");
        }
    }
}
