using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T05Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                schema: "knowledge",
                table: "question",
                type: "datetime2(3)",
                nullable: true);

            // Existing questions pre-date explicit activity tracking. Bounded batches keep the lock and log footprint
            // suitable for SQL Server Express; asked_at is the only activity every old row is guaranteed to have.
            migrationBuilder.Sql("""
                SET NOCOUNT ON;
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (10000) [knowledge].[question]
                       SET [updated_at] = [asked_at]
                     WHERE [updated_at] IS NULL;
                    IF @@ROWCOUNT = 0 BREAK;
                END;
                """, suppressTransaction: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "updated_at",
                schema: "knowledge",
                table: "question",
                type: "datetime2(3)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "question_digest",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    digest_date = table.Column<DateOnly>(type: "date", nullable: false),
                    mail_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    queued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_digest", x => new { x.tenant_id, x.user_id, x.digest_date });
                });

            migrationBuilder.CreateTable(
                name: "question_notification",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    notification_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_kind = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    delivery_mode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    mail_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    digest_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_notification", x => new { x.tenant_id, x.notification_id });
                    table.CheckConstraint("ck_question_notification_delivery_mode", "delivery_mode IN ('immediately','daily_digest')");
                    table.ForeignKey(
                        name: "fk_question_notification_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question_notification_preference",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    mode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_notification_preference", x => new { x.tenant_id, x.user_id });
                    table.CheckConstraint("ck_question_notification_preference_mode", "mode IN ('immediately','daily_digest','off')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_status_updated_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "status", "updated_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_digest_tenant_id_digest_date_user_id",
                schema: "knowledge",
                table: "question_digest",
                columns: new[] { "tenant_id", "digest_date", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_notification_tenant_id_delivery_mode_digest_date_recipient_user_id_occurred_at",
                schema: "knowledge",
                table: "question_notification",
                columns: new[] { "tenant_id", "delivery_mode", "digest_date", "recipient_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_question_notification_tenant_id_question_id",
                schema: "knowledge",
                table: "question_notification",
                columns: new[] { "tenant_id", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ux_question_notification_tenant_id_recipient_user_id_event_kind_event_id",
                schema: "knowledge",
                table: "question_notification",
                columns: new[] { "tenant_id", "recipient_user_id", "event_kind", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_question_notification_preference_tenant_id_mode_user_id",
                schema: "knowledge",
                table: "question_notification_preference",
                columns: new[] { "tenant_id", "mode", "user_id" });

            foreach (var table in new[] { "question_digest", "question_notification", "question_notification_preference" })
                migrationBuilder.AddTenantPolicy("knowledge", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "question_notification_preference", "question_notification", "question_digest" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);

            migrationBuilder.DropTable(
                name: "question_digest",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_notification",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_notification_preference",
                schema: "knowledge");

            migrationBuilder.DropIndex(
                name: "ix_question_tenant_id_status_updated_at_question_id",
                schema: "knowledge",
                table: "question");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "knowledge",
                table: "question");
        }
    }
}
