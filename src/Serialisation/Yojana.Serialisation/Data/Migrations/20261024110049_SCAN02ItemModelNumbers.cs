using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class SCAN02ItemModelNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_serialisation_settings",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    model_numbers_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    last_modified_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_modified_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_serialisation_settings", x => new { x.tenant_id, x.item_id });
                    table.CheckConstraint("ck_item_serialisation_settings_model_numbers_json", "ISJSON(model_numbers_json) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_serialisation_settings_tenant_id_last_modified_at",
                schema: "serialisation",
                table: "item_serialisation_settings",
                columns: new[] { "tenant_id", "last_modified_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "item_serialisation_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_serialisation_settings",
                schema: "serialisation");
        }
    }
}
