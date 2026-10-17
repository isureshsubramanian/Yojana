using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P4T09DocumentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "recovery_number",
                schema: "serialisation",
                table: "recovery_claim",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "claim_number",
                schema: "serialisation",
                table: "claim",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "number_series",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    series = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    year = table.Column<int>(type: "int", nullable: false),
                    last_value = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_number_series", x => new { x.tenant_id, x.series, x.year });
                    table.CheckConstraint("ck_number_series_last_value", "last_value >= 0");
                });

            // Number the claims and recovery claims recorded before numbering existed, in submission order per tenant and
            // year, and start each series after them. Row-level security hides every row from a session with no tenant,
            // so the backfill switches the schema's policy off for its own statements only.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT claim_number, CONCAT('CLM-', YEAR(submitted_at), '-', RIGHT(CONCAT('00000', ROW_NUMBER() OVER (PARTITION BY tenant_id, YEAR(submitted_at) ORDER BY submitted_at, claim_id)), 5)) AS issued
                    FROM [serialisation].[claim])
                UPDATE numbered SET claim_number = issued;
                WITH numbered AS (
                    SELECT recovery_number, CONCAT('RCV-', YEAR(raised_at), '-', RIGHT(CONCAT('0000', ROW_NUMBER() OVER (PARTITION BY tenant_id, YEAR(raised_at) ORDER BY raised_at, recovery_claim_id)), 4)) AS issued
                    FROM [serialisation].[recovery_claim])
                UPDATE numbered SET recovery_number = issued;
                INSERT INTO [serialisation].[number_series] (tenant_id, series, [year], last_value)
                SELECT tenant_id, 'CLM', YEAR(submitted_at), COUNT(*) FROM [serialisation].[claim] GROUP BY tenant_id, YEAR(submitted_at)
                UNION ALL
                SELECT tenant_id, 'RCV', YEAR(raised_at), COUNT(*) FROM [serialisation].[recovery_claim] GROUP BY tenant_id, YEAR(raised_at);
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");
            migrationBuilder.AddTenantPolicy("serialisation", "number_series");

            migrationBuilder.CreateIndex(
                name: "ux_recovery_claim_tenant_id_recovery_number",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "recovery_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_claim_tenant_id_claim_number",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "claim_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "number_series");
            migrationBuilder.DropTable(
                name: "number_series",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ux_recovery_claim_tenant_id_recovery_number",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropIndex(
                name: "ux_claim_tenant_id_claim_number",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropColumn(
                name: "recovery_number",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropColumn(
                name: "claim_number",
                schema: "serialisation",
                table: "claim");
        }
    }
}
