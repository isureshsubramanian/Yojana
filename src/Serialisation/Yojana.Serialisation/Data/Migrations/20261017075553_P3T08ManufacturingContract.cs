using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T08ManufacturingContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manufacturing_message_receipt",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    payload_hash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    result_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    processed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manufacturing_message_receipt", x => new { x.tenant_id, x.message_id });
                    table.CheckConstraint("ck_manufacturing_message_receipt_result_json", "ISJSON(result_json) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_manufacturing_message_receipt_tenant_id_processed_at",
                schema: "serialisation",
                table: "manufacturing_message_receipt",
                columns: new[] { "tenant_id", "processed_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "manufacturing_message_receipt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "manufacturing_message_receipt");

            migrationBuilder.DropTable(
                name: "manufacturing_message_receipt",
                schema: "serialisation");
        }
    }
}
