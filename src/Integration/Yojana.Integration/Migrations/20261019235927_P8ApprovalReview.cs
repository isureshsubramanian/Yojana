using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8ApprovalReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_remote_key",
                schema: "integration",
                table: "outbound_command",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "integration",
                table: "manual_outbound_task",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_remote_key",
                schema: "integration",
                table: "outbound_command");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "integration",
                table: "manual_outbound_task");
        }
    }
}
