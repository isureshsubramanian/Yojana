using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8CoreFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inbound_message_tenant_id_translation_status_accepted_at_inbound_message_id",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbound_message_translation_status",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.AddColumn<string>(
                name: "attention_reason_code",
                schema: "integration",
                table: "inbound_message",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_translation_error",
                schema: "integration",
                table: "inbound_message",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_translation_attempt_at",
                schema: "integration",
                table: "inbound_message",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "translation_attempt_count",
                schema: "integration",
                table: "inbound_message",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "translation_started_at",
                schema: "integration",
                table: "inbound_message",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbound_message_tenant_id_translation_status_next_translation_attempt_at_accepted_at_inbound_message_id",
                schema: "integration",
                table: "inbound_message",
                columns: new[] { "tenant_id", "translation_status", "next_translation_attempt_at", "accepted_at", "inbound_message_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbound_message_translation_status",
                schema: "integration",
                table: "inbound_message",
                sql: "translation_status IN ('pending','processing','held','failed','superseded','completed','late','needs_attention')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inbound_message_tenant_id_translation_status_next_translation_attempt_at_accepted_at_inbound_message_id",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbound_message_translation_status",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropColumn(
                name: "attention_reason_code",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropColumn(
                name: "last_translation_error",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropColumn(
                name: "next_translation_attempt_at",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropColumn(
                name: "translation_attempt_count",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.DropColumn(
                name: "translation_started_at",
                schema: "integration",
                table: "inbound_message");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_message_tenant_id_translation_status_accepted_at_inbound_message_id",
                schema: "integration",
                table: "inbound_message",
                columns: new[] { "tenant_id", "translation_status", "accepted_at", "inbound_message_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbound_message_translation_status",
                schema: "integration",
                table: "inbound_message",
                sql: "translation_status IN ('pending','superseded','completed','late')");
        }
    }
}
