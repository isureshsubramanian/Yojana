using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T18ImportFileArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "converted_file_cleanup_reference",
                schema: "serialisation",
                table: "import_job",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_file_cleanup_reference",
                schema: "serialisation",
                table: "import_job",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "converted_file_cleanup_reference",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropColumn(
                name: "original_file_cleanup_reference",
                schema: "serialisation",
                table: "import_job");
        }
    }
}
