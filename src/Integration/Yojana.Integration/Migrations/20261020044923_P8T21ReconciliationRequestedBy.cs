using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T21ReconciliationRequestedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                schema: "integration",
                table: "reconciliation_run",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_by",
                schema: "integration",
                table: "reconciliation_run");
        }
    }
}
