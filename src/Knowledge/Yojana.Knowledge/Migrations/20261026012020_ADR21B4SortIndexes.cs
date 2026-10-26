using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class ADR21B4SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_status_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "status", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_tenant_id_published_at_entry_id",
                schema: "knowledge",
                table: "knowledge_entry",
                columns: new[] { "tenant_id", "published_at", "entry_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_question_tenant_id_status_question_id",
                schema: "knowledge",
                table: "question");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_entry_tenant_id_published_at_entry_id",
                schema: "knowledge",
                table: "knowledge_entry");
        }
    }
}
