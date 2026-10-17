using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4ClaimCampaign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "labour_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "parts_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "travel_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            // Existing campaigns pre-date configurable cover. Row-level security hides their rows from a migration
            // session with no tenant, so switch the policy off for this backfill statement only.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("UPDATE [serialisation].[campaign] SET [parts_pct] = 100, [labour_pct] = 100, [travel_pct] = 100 WHERE [parts_pct] IS NULL OR [labour_pct] IS NULL OR [travel_pct] IS NULL");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AlterColumn<decimal>(
                name: "labour_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldPrecision: 5,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "parts_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldPrecision: 5,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "travel_pct",
                schema: "serialisation",
                table: "campaign",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldPrecision: 5,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_cover_pct",
                schema: "serialisation",
                table: "campaign",
                sql: "parts_pct BETWEEN 0 AND 100 AND labour_pct BETWEEN 0 AND 100 AND travel_pct BETWEEN 0 AND 100");

            migrationBuilder.AddColumn<string>(
                name: "campaign_cover_reason",
                schema: "serialisation",
                table: "claim",
                type: "varchar(40)",
                unicode: false,
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                schema: "serialisation",
                table: "claim",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reserve_tag",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_campaign_id",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "campaign_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_cover_pct",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "labour_pct",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "parts_pct",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropColumn(
                name: "travel_pct",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropIndex(
                name: "ix_claim_tenant_id_campaign_id",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "campaign_cover_reason",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "reserve_tag",
                schema: "serialisation",
                table: "claim");
        }
    }
}
