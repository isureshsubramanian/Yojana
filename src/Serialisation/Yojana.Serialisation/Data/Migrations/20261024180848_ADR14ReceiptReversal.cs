using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class ADR14ReceiptReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_unit_current_lifecycle_status",
                schema: "serialisation",
                table: "unit_current",
                sql: "lifecycle_status IN ('pending_identifier','pre_release','void','released','dormant','scrapped_pre_release','scrapped','decommissioned','reversed','merged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_unit_lifecycle_status",
                schema: "serialisation",
                table: "unit",
                sql: "lifecycle_status IN ('pending_identifier','pre_release','void','released','dormant','scrapped_pre_release','scrapped','decommissioned','reversed','merged')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_unit_current_lifecycle_status",
                schema: "serialisation",
                table: "unit_current");

            migrationBuilder.DropCheckConstraint(
                name: "ck_unit_lifecycle_status",
                schema: "serialisation",
                table: "unit");
        }
    }
}
