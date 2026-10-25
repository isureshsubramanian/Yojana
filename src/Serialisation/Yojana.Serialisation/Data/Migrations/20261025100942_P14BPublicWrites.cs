using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P14BPublicWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "attachment_content",
                schema: "serialisation",
                table: "claim_evidence",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_content_type",
                schema: "serialisation",
                table: "claim_evidence",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_file_name",
                schema: "serialisation",
                table: "claim_evidence",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachment_content",
                schema: "serialisation",
                table: "claim_evidence");

            migrationBuilder.DropColumn(
                name: "attachment_content_type",
                schema: "serialisation",
                table: "claim_evidence");

            migrationBuilder.DropColumn(
                name: "attachment_file_name",
                schema: "serialisation",
                table: "claim_evidence");
        }
    }
}
