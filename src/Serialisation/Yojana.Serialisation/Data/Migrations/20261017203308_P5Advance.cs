using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Advance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_warranty_replacement_source_kind",
                schema: "serialisation",
                table: "warranty_replacement");

            migrationBuilder.DropCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.AddColumn<Guid>(
                name: "bill_command_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "billed_amount",
                schema: "serialisation",
                table: "replacement_link",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "billed_at",
                schema: "serialisation",
                table: "replacement_link",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billed_by",
                schema: "serialisation",
                table: "replacement_link",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "billed_on",
                schema: "serialisation",
                table: "replacement_link",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billed_price_basis",
                schema: "serialisation",
                table: "replacement_link",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "counterparty_party_ref",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "non_return_amount",
                schema: "serialisation",
                table: "replacement_link",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "overdue_event_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "overdue_published_at",
                schema: "serialisation",
                table: "replacement_link",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "return_due_command_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "party_replacement_hold",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_replacement_hold_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reason = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    set_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    set_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    set_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    lifted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    lifted_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    lift_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    lift_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_party_replacement_hold", x => new { x.tenant_id, x.party_replacement_hold_id });
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_warranty_replacement_source_kind",
                schema: "serialisation",
                table: "warranty_replacement",
                sql: "source_kind IN ('claim', 'rma', 'loan')");

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_bill_command_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "bill_command_id" },
                unique: true,
                filter: "[bill_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_overdue_event_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "overdue_event_id" },
                unique: true,
                filter: "[overdue_event_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_return_due_command_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "return_due_command_id" },
                unique: true,
                filter: "[return_due_command_id] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link",
                sql: "source_kind IN ('claim', 'rma', 'loan')");

            migrationBuilder.CreateIndex(
                name: "ix_party_replacement_hold_tenant_id_party_ref_lifted_at",
                schema: "serialisation",
                table: "party_replacement_hold",
                columns: new[] { "tenant_id", "party_ref", "lifted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_party_replacement_hold_tenant_id_lift_command_id",
                schema: "serialisation",
                table: "party_replacement_hold",
                columns: new[] { "tenant_id", "lift_command_id" },
                unique: true,
                filter: "[lift_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_party_replacement_hold_tenant_id_party_ref",
                schema: "serialisation",
                table: "party_replacement_hold",
                columns: new[] { "tenant_id", "party_ref" },
                unique: true,
                filter: "[lifted_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_party_replacement_hold_tenant_id_set_command_id",
                schema: "serialisation",
                table: "party_replacement_hold",
                columns: new[] { "tenant_id", "set_command_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "party_replacement_hold");

            // Existing customer-facing links predate the explicit counterparty column. RLS hides all tenants from a
            // migration session, so this one backfill disables the policy for its own statement and immediately restores it.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                UPDATE link
                SET counterparty_party_ref = entitlement.owner_party_ref
                FROM [serialisation].[replacement_link] link
                INNER JOIN [serialisation].[entitlement] entitlement
                    ON entitlement.tenant_id = link.tenant_id AND entitlement.entitlement_id = link.source_entitlement_id
                WHERE link.direction = 'customer_facing' AND link.counterparty_party_ref IS NULL;
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "party_replacement_hold");

            migrationBuilder.DropTable(
                name: "party_replacement_hold",
                schema: "serialisation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_warranty_replacement_source_kind",
                schema: "serialisation",
                table: "warranty_replacement");

            migrationBuilder.DropIndex(
                name: "ux_replacement_link_tenant_id_bill_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ux_replacement_link_tenant_id_overdue_event_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ux_replacement_link_tenant_id_return_due_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "bill_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "billed_amount",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "billed_at",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "billed_by",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "billed_on",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "billed_price_basis",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "counterparty_party_ref",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "non_return_amount",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "overdue_event_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "overdue_published_at",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "return_due_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.AddCheckConstraint(
                name: "ck_warranty_replacement_source_kind",
                schema: "serialisation",
                table: "warranty_replacement",
                sql: "source_kind IN ('claim', 'rma')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_replacement_link_source_kind",
                schema: "serialisation",
                table: "replacement_link",
                sql: "source_kind IN ('claim', 'rma')");
        }
    }
}
