using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4T04LegacyUnknownClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adjudicated_by",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewer_role",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "adjudicated_by",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "reviewer_role",
                schema: "serialisation",
                table: "claim");
        }
    }
}
