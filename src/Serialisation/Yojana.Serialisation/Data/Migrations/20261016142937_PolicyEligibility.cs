using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class PolicyEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "eligibility",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_warranty_policy_version_eligibility_json",
                schema: "serialisation",
                table: "warranty_policy_version",
                sql: "eligibility IS NULL OR ISJSON(eligibility) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_warranty_policy_version_eligibility_json",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "eligibility",
                schema: "serialisation",
                table: "warranty_policy_version");
        }
    }
}
