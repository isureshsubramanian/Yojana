using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Erase02PrivacyRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_erasure_request_status",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.AddColumn<string>(
                name: "refusal_records_json",
                schema: "platform",
                table: "erasure_request",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateOnly>(
                name: "request_received_on",
                schema: "platform",
                table: "erasure_request",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (5000) platform.erasure_request
                    SET request_received_on = CONVERT(date, requested_at)
                    WHERE request_received_on IS NULL;
                    IF @@ROWCOUNT = 0 BREAK;
                END
                """);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "request_received_on",
                schema: "platform",
                table: "erasure_request",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_erasure_request_status",
                schema: "platform",
                table: "erasure_request",
                sql: "status IN ('pending','running','completed','failed','refused')");

            migrationBuilder.CreateIndex(
                name: "ix_erasure_request_tenant_id_status_request_received_on_erasure_id",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "status", "request_received_on", "erasure_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_erasure_request_status",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.DropIndex(
                name: "ix_erasure_request_tenant_id_status_request_received_on_erasure_id",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.DropColumn(
                name: "refusal_records_json",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.DropColumn(
                name: "request_received_on",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.AddCheckConstraint(
                name: "ck_erasure_request_status",
                schema: "platform",
                table: "erasure_request",
                sql: "status IN ('pending','running','completed','failed')");
        }
    }
}
