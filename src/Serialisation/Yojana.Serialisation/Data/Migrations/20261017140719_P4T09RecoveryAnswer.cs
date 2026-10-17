using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4T09RecoveryAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vendor_note",
                schema: "serialisation",
                table: "recovery_claim",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vendor_reference",
                schema: "serialisation",
                table: "recovery_claim",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vendor_note",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropColumn(
                name: "vendor_reference",
                schema: "serialisation",
                table: "recovery_claim");
        }
    }
}
