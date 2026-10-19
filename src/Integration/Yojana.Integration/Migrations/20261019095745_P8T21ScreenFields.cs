using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T21ScreenFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "desk",
                schema: "integration",
                table: "manual_outbound_task",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [integration].[manual_outbound_task]
                SET [desk] = CASE
                    WHEN [command_type] IN ('CreateCreditMemo', 'PostWarrantyExpense') THEN 'finance'
                    WHEN [command_type] IN ('CreateReplacementDelivery', 'ChangeStockStatusOrLocation') THEN 'warehouse'
                    ELSE 'service'
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "desk",
                schema: "integration",
                table: "manual_outbound_task",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_outbound_task_tenant_id_status_desk_created_at_manual_outbound_task_id",
                schema: "integration",
                table: "manual_outbound_task",
                columns: new[] { "tenant_id", "status", "desk", "created_at", "manual_outbound_task_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_manual_outbound_task_tenant_id_status_desk_created_at_manual_outbound_task_id",
                schema: "integration",
                table: "manual_outbound_task");

            migrationBuilder.DropColumn(
                name: "desk",
                schema: "integration",
                table: "manual_outbound_task");
        }
    }
}
