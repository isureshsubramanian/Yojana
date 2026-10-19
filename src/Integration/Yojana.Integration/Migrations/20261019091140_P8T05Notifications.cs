using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T05Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_notification_source",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "bit", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_notification_source", x => new { x.tenant_id, x.connection_id, x.source });
                });

            migrationBuilder.CreateTable(
                name: "connection_poll_watermark",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    watermark = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    overlap_seconds = table.Column<int>(type: "int", nullable: false),
                    interval_seconds = table.Column<int>(type: "int", nullable: false),
                    next_poll_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_polled_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_poll_watermark", x => new { x.tenant_id, x.connection_id, x.entity_type });
                    table.CheckConstraint("ck_connection_poll_interval", "interval_seconds BETWEEN 1 AND 86400");
                    table.CheckConstraint("ck_connection_poll_overlap", "overlap_seconds BETWEEN 0 AND 86400");
                    table.CheckConstraint("ck_connection_poll_revision", "revision >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_notification_source_tenant_id_connection_id_enabled_source",
                schema: "integration",
                table: "connection_notification_source",
                columns: new[] { "tenant_id", "connection_id", "enabled", "source" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_poll_watermark_tenant_id_next_poll_at_connection_id_entity_type",
                schema: "integration",
                table: "connection_poll_watermark",
                columns: new[] { "tenant_id", "next_poll_at", "connection_id", "entity_type" });

            migrationBuilder.AddTenantPolicy("integration", "connection_notification_source");
            migrationBuilder.AddTenantPolicy("integration", "connection_poll_watermark");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connection_notification_source");
            migrationBuilder.RemoveTenantPolicy("integration", "connection_poll_watermark");
            migrationBuilder.DropTable(
                name: "connection_notification_source",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_poll_watermark",
                schema: "integration");
        }
    }
}
