using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T30CoverPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_validation_check_result",
                schema: "migration",
                table: "migration_validation_check");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_validation_check_result",
                schema: "migration",
                table: "migration_validation_check",
                sql: "result IN ('pass','explained','fail','hard_fail','report','warning')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_validation_check_result",
                schema: "migration",
                table: "migration_validation_check");

            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_validation_check_result",
                schema: "migration",
                table: "migration_validation_check",
                sql: "result IN ('pass','explained','fail','hard_fail','report')");
        }
    }
}
