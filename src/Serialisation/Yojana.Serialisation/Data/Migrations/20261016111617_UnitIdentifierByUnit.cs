using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnitIdentifierByUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_unit_id",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "unit_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_identifier_tenant_id_unit_id",
                schema: "serialisation",
                table: "unit_identifier");
        }
    }
}
