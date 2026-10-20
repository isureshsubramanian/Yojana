using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T23MailTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally global like security_event: account mail about a global identity is sent before a tenant is
            // chosen, so there is no tenant to put in SESSION_CONTEXT. Readers filter by tenant (MailEndpoints). No body column:
            // account mail carries single-use links, which must not outlive delivery.
            migrationBuilder.CreateTable(
                name: "mail_message",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    queued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    recipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    transport = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    sent_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    provider_message_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_mail_message_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_mail_message_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_message_queued_at_id",
                schema: "platform",
                table: "mail_message",
                columns: new[] { "queued_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_message_tenant_id_queued_at_id",
                schema: "platform",
                table: "mail_message",
                columns: new[] { "tenant_id", "queued_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_mail_message_user_id_queued_at",
                schema: "platform",
                table: "mail_message",
                columns: new[] { "user_id", "queued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_message",
                schema: "platform");
        }
    }
}
