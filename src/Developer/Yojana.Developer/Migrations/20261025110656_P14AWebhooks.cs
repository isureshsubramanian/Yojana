using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Developer.Migrations
{
    /// <inheritdoc />
    public partial class P14AWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "item_family_id",
                schema: "developer",
                table: "public_event");

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'developer.webhook_delivery') AND name = N'ux_webhook_delivery_tenant_id_subscription_id_event_id')
                BEGIN
                    DROP INDEX [ux_webhook_delivery_tenant_id_subscription_id_event_id] ON [developer].[webhook_delivery];
                END;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'developer.webhook_delivery') AND name = N'ux_webhook_delivery_first')
                BEGIN
                    CREATE UNIQUE INDEX [ux_webhook_delivery_first] ON [developer].[webhook_delivery] ([tenant_id], [subscription_id], [event_id]) WHERE [replay_of] IS NULL;
                END;
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "retry_window_started_at",
                schema: "developer",
                table: "webhook_delivery",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "item_family",
                schema: "developer",
                table: "public_event",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "retry_window_started_at",
                schema: "developer",
                table: "webhook_delivery");

            migrationBuilder.DropColumn(
                name: "item_family",
                schema: "developer",
                table: "public_event");

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'developer.webhook_delivery') AND name = N'ux_webhook_delivery_first')
                BEGIN
                    DROP INDEX [ux_webhook_delivery_first] ON [developer].[webhook_delivery];
                END;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'developer.webhook_delivery') AND name = N'ux_webhook_delivery_tenant_id_subscription_id_event_id')
                BEGIN
                    CREATE UNIQUE INDEX [ux_webhook_delivery_tenant_id_subscription_id_event_id] ON [developer].[webhook_delivery] ([tenant_id], [subscription_id], [event_id]) WHERE [replay_of] IS NULL;
                END;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "item_family_id",
                schema: "developer",
                table: "public_event",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}
