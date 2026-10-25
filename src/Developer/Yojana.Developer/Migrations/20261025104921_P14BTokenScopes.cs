using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Developer.Migrations
{
    /// <inheritdoc />
    public partial class P14BTokenScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scopes",
                schema: "developer",
                table: "api_access_token",
                type: "varchar(1000)",
                unicode: false,
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scopes",
                schema: "developer",
                table: "api_access_token");
        }
    }
}
