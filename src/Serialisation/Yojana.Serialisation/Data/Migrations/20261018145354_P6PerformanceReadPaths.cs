using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6PerformanceReadPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_identifier_tenant_id_normalised_value",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_normalised_value_type_unit_id",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "normalised_value", "type", "unit_id" })
                .Annotation("SqlServer:Include", new[] { "scheme", "raw_value", "scope_key", "valid_to", "superseded_by_identifier_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_identifier_tenant_id_normalised_value_type_unit_id",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_normalised_value",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "normalised_value" })
                .Annotation("SqlServer:Include", new[] { "unit_id", "type", "scope_key", "valid_to" });
        }
    }
}
