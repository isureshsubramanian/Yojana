using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4CampaignReasonRunsUntil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                schema: "serialisation",
                table: "campaign",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            // Campaigns created before reasons were captured retain a valid, explicit classification.
            migrationBuilder.Sql("UPDATE [serialisation].[campaign] SET [reason_code] = 'safety' WHERE [reason_code] IS NULL");

            migrationBuilder.AlterColumn<string>(
                name: "reason_code",
                schema: "serialisation",
                table: "campaign",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldUnicode: false,
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_note",
                schema: "serialisation",
                table: "campaign",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "runs_until",
                schema: "serialisation",
                table: "campaign",
                type: "date",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_reason_code",
                schema: "serialisation",
                table: "campaign",
                sql: "reason_code IN ('safety', 'reliability', 'firmware', 'regulatory', 'goodwill')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_reason_code",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "reason_code",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "reason_note",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "runs_until",
                schema: "serialisation",
                table: "campaign");
        }
    }
}
