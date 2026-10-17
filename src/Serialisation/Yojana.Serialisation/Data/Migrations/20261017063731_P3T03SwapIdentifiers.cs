using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T03SwapIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "swapped_with_identifier_id",
                schema: "serialisation",
                table: "unit_identifier",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_swapped_with_identifier_id",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "swapped_with_identifier_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_identifier_tenant_id_swapped_with_identifier_id",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.DropColumn(
                name: "swapped_with_identifier_id",
                schema: "serialisation",
                table: "unit_identifier");
        }
    }
}
