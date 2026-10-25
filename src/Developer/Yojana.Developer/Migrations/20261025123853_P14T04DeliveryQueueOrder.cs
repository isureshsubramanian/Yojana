using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Developer.Migrations
{
    /// <inheritdoc />
    public partial class P14T04DeliveryQueueOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "seq_webhook_delivery",
                schema: "developer");

            migrationBuilder.AddColumn<long>(
                name: "queue_position",
                schema: "developer",
                table: "webhook_delivery",
                type: "bigint",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR [developer].[seq_webhook_delivery]");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_status_queue_position",
                schema: "developer",
                table: "webhook_delivery",
                columns: new[] { "tenant_id", "subscription_id", "status", "queue_position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_status_queue_position",
                schema: "developer",
                table: "webhook_delivery");

            migrationBuilder.DropColumn(
                name: "queue_position",
                schema: "developer",
                table: "webhook_delivery");

            migrationBuilder.DropSequence(
                name: "seq_webhook_delivery",
                schema: "developer");
        }
    }
}
