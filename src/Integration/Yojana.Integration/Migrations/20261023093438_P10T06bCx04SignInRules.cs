using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P10T06bCx04SignInRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reminder_days_sent_json",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql("""
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (1000) integration.cx04_authorisation_state
                    SET reminder_days_sent_json = CASE
                        WHEN reminder14_days_sent = 1 AND reminder3_days_sent = 1 THEN '[14,3]'
                        WHEN reminder14_days_sent = 1 THEN '[14]'
                        WHEN reminder3_days_sent = 1 THEN '[3]'
                        ELSE '[]'
                    END
                    WHERE reminder_days_sent_json = '[]'
                      AND (reminder14_days_sent = 1 OR reminder3_days_sent = 1);
                    IF @@ROWCOUNT = 0 BREAK;
                END
                """);

            migrationBuilder.DropColumn(
                name: "reminder14_days_sent",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "reminder3_days_sent",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.AddColumn<DateTime>(
                name: "disabled_at",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_by",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "disabled_reason",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_call_at",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pause_notified_at",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pause_reason",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "signed_in_at",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signed_in_by",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "reminder14_days_sent",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "reminder3_days_sent",
                schema: "integration",
                table: "cx04_authorisation_state",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE integration.cx04_authorisation_state
                SET reminder14_days_sent = CASE WHEN EXISTS (
                        SELECT 1 FROM OPENJSON(reminder_days_sent_json) WHERE TRY_CONVERT(int, [value]) = 14
                    ) THEN 1 ELSE 0 END,
                    reminder3_days_sent = CASE WHEN EXISTS (
                        SELECT 1 FROM OPENJSON(reminder_days_sent_json) WHERE TRY_CONVERT(int, [value]) = 3
                    ) THEN 1 ELSE 0 END;
                """);

            migrationBuilder.DropColumn(
                name: "disabled_at",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "disabled_by",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "disabled_reason",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "last_call_at",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "pause_notified_at",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "pause_reason",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "reminder_days_sent_json",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "signed_in_at",
                schema: "integration",
                table: "cx04_authorisation_state");

            migrationBuilder.DropColumn(
                name: "signed_in_by",
                schema: "integration",
                table: "cx04_authorisation_state");

        }
    }
}
