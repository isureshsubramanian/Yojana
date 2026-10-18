using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6ConcurrencyPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "list_order_bucket",
                schema: "serialisation",
                table: "claim",
                type: "int",
                nullable: false,
                computedColumnSql: "CASE WHEN [status] IN ('settled', 'closed') THEN 1 ELSE 0 END",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_list_order_bucket_submitted_at_claim_id",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "list_order_bucket", "submitted_at", "claim_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_claim_tenant_id_list_order_bucket_submitted_at_claim_id",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "list_order_bucket",
                schema: "serialisation",
                table: "claim");
        }
    }
}
