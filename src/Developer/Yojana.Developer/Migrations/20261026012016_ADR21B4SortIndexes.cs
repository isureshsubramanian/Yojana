using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Developer.Migrations
{
    /// <inheritdoc />
    public partial class ADR21B4SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_status_delivery_id",
                schema: "developer",
                table: "webhook_delivery",
                columns: new[] { "tenant_id", "subscription_id", "status", "delivery_id" });

            migrationBuilder.CreateIndex(
                name: "ix_api_client_tenant_id_status_client_id",
                schema: "developer",
                table: "api_client",
                columns: new[] { "tenant_id", "status", "client_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_status_delivery_id",
                schema: "developer",
                table: "webhook_delivery");

            migrationBuilder.DropIndex(
                name: "ix_api_client_tenant_id_status_client_id",
                schema: "developer",
                table: "api_client");
        }
    }
}
