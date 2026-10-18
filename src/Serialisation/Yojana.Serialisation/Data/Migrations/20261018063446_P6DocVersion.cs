using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6DocVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "warranty_replacement",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "warranty_replacement",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "warranty_replacement",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "rma",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "rma",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "rma",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "return_reason",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "return_reason",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "return_reason",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "replacement_policy",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "replacement_policy",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "replacement_policy",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "refurbishment_work_order",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "refurbishment_work_order",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "refurbishment_work_order",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "recovery_claim",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "recovery_claim",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "recovery_claim",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "coverage_family",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "coverage_family",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "coverage_family",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_modified_at",
                schema: "serialisation",
                table: "claim",
                type: "datetime2(3)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_modified_by",
                schema: "serialisation",
                table: "claim",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "claim",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "row_version",
                schema: "serialisation",
                table: "campaign",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                UPDATE [serialisation].[warranty_replacement]
                SET [last_modified_at] = COALESCE([shipped_at], [created_at]),
                    [last_modified_by] = COALESCE([shipped_by], [created_by]);
                UPDATE [serialisation].[warranty_policy_version]
                SET [last_modified_at] = COALESCE([published_at], [created_at]),
                    [last_modified_by] = COALESCE([published_by], [created_by]);
                UPDATE [serialisation].[rma]
                SET [last_modified_at] = COALESCE([closed_at], [dispositioned_at], [received_at], [in_transit_at], [authorised_at], [requested_at]),
                    [last_modified_by] = COALESCE([last_actor_ref], [requested_by]);
                UPDATE [serialisation].[return_reason]
                SET [last_modified_at] = [created_at], [last_modified_by] = 'system:migration';
                UPDATE [serialisation].[replacement_policy]
                SET [last_modified_at] = [created_at], [last_modified_by] = 'system:migration';
                UPDATE [serialisation].[refurbishment_work_order]
                SET [last_modified_at] = COALESCE([cancelled_at], [released_at], [opened_at]),
                    [last_modified_by] = COALESCE([cancelled_by], [released_by], [opened_by]);
                UPDATE recovery
                SET [last_modified_at] = COALESCE(recovery.[approved_at], recovery.[raised_at]),
                    [last_modified_by] = COALESCE(recovery.[approved_by], claim_row.[submitted_by])
                FROM [serialisation].[recovery_claim] recovery
                INNER JOIN [serialisation].[claim] claim_row
                    ON claim_row.[tenant_id] = recovery.[tenant_id]
                    AND claim_row.[claim_id] = recovery.[customer_claim_id];
                UPDATE [serialisation].[coverage_family]
                SET [last_modified_at] = [created_at], [last_modified_by] = 'system:migration';
                UPDATE [serialisation].[claim]
                SET [last_modified_at] = COALESCE([closed_at], [settled_at], [adjudicated_at], [submitted_at]),
                    [last_modified_by] = COALESCE([adjudicated_by], [submitted_by]);
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "warranty_replacement");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "warranty_replacement");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "warranty_replacement");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "return_reason");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "return_reason");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "return_reason");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "replacement_policy");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "replacement_policy");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "replacement_policy");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "refurbishment_work_order");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "refurbishment_work_order");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "refurbishment_work_order");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "coverage_family");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "coverage_family");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "coverage_family");

            migrationBuilder.DropColumn(
                name: "last_modified_at",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "last_modified_by",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "serialisation",
                table: "campaign");
        }
    }
}
