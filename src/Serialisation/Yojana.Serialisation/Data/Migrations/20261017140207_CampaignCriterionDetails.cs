using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class CampaignCriterionDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_shape",
                schema: "serialisation",
                table: "campaign_criteria");

            migrationBuilder.AddColumn<Guid>(
                name: "child_lot_id",
                schema: "serialisation",
                table: "campaign_criteria",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "firmware_key",
                schema: "serialisation",
                table: "campaign_criteria",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // Row-level security hides every row from a session with no tenant; the backfill switches the schema's policy
            // off for its own statement so existing firmware criteria pass the new shape check.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("UPDATE [serialisation].[campaign_criteria] SET [firmware_key] = N'firmware' WHERE [kind] = N'firmware'");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_shape",
                schema: "serialisation",
                table: "campaign_criteria",
                sql: "(kind = 'identifier_range' AND scheme IS NOT NULL AND identifier_type IS NOT NULL AND range_start IS NOT NULL AND range_end IS NOT NULL AND range_start_ordinal IS NOT NULL AND range_end_ordinal IS NOT NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'lot' AND lot_id IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'child' AND ((child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)) AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND firmware_key IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'firmware' AND firmware_key IS NOT NULL AND firmware_value IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND child_lot_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_campaign_criteria_shape",
                schema: "serialisation",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "child_lot_id",
                schema: "serialisation",
                table: "campaign_criteria");

            migrationBuilder.DropColumn(
                name: "firmware_key",
                schema: "serialisation",
                table: "campaign_criteria");

            migrationBuilder.AddCheckConstraint(
                name: "ck_campaign_criteria_shape",
                schema: "serialisation",
                table: "campaign_criteria",
                sql: "(kind = 'identifier_range' AND scheme IS NOT NULL AND identifier_type IS NOT NULL AND range_start IS NOT NULL AND range_end IS NOT NULL AND range_start_ordinal IS NOT NULL AND range_end_ordinal IS NOT NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'lot' AND lot_id IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'child' AND ((child_unit_id IS NOT NULL AND child_item_id IS NULL) OR (child_unit_id IS NULL AND child_item_id IS NOT NULL)) AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND firmware_value IS NULL AND effective_at IS NULL)\nOR (kind = 'firmware' AND firmware_value IS NOT NULL AND scheme IS NULL AND identifier_type IS NULL AND range_start IS NULL AND range_end IS NULL AND range_start_ordinal IS NULL AND range_end_ordinal IS NULL AND lot_id IS NULL AND child_unit_id IS NULL AND child_item_id IS NULL)");
        }
    }
}
