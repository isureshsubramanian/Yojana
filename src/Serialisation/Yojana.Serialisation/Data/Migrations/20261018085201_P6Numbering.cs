using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Numbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "number_series_setting",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    series = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    mode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    last_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_number_series_setting", x => new { x.tenant_id, x.series });
                    table.CheckConstraint("ck_number_series_setting_mode", "mode IN ('sequence', 'gap_free')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_number_series_setting_tenant_id_mode_series",
                schema: "serialisation",
                table: "number_series_setting",
                columns: new[] { "tenant_id", "mode", "series" });

            migrationBuilder.AddTenantPolicy("serialisation", "number_series_setting");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "number_series_setting");
            migrationBuilder.DropTable(
                name: "number_series_setting",
                schema: "serialisation");
        }
    }
}
