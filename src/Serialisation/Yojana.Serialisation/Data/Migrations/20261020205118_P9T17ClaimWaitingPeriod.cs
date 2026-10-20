using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T17ClaimWaitingPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "no_claim_within_days_of_purchase",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "purchase_date",
                schema: "serialisation",
                table: "entitlement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "first_claimable_date",
                schema: "serialisation",
                table: "claim_line",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waiting_period_not_applied_reason",
                schema: "serialisation",
                table: "claim_line",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "waiting_period_purchase_date",
                schema: "serialisation",
                table: "claim_line",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "no_claim_within_days_of_purchase",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "purchase_date",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "first_claimable_date",
                schema: "serialisation",
                table: "claim_line");

            migrationBuilder.DropColumn(
                name: "waiting_period_not_applied_reason",
                schema: "serialisation",
                table: "claim_line");

            migrationBuilder.DropColumn(
                name: "waiting_period_purchase_date",
                schema: "serialisation",
                table: "claim_line");
        }
    }
}
