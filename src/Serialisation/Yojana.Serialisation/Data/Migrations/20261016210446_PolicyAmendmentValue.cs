using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class PolicyAmendmentValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "amendment_value",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_warranty_policy_version_amendment_value_json",
                schema: "serialisation",
                table: "warranty_policy_version",
                sql: "amendment_value IS NULL OR ISJSON(amendment_value) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_warranty_policy_version_amendment_value_json",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "amendment_value",
                schema: "serialisation",
                table: "warranty_policy_version");
        }
    }
}
