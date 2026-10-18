using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Polish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_template_form_type",
                schema: "serialisation",
                table: "certificate_template");

            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_instance_form_type",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.AddColumn<string>(
                name: "job_number",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            // Existing attempts receive stable numbers in their original request order. RLS hides tenant rows from the
            // migration session, so this statement disables the policy only for the bounded backfill and counter seed.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT job_number,
                        CONCAT('PJ-', YEAR(requested_at), '-', RIGHT(CONCAT('00000', ROW_NUMBER() OVER (
                            PARTITION BY tenant_id, YEAR(requested_at) ORDER BY requested_at, job_id)), 5)) AS issued
                    FROM [serialisation].[print_job])
                UPDATE numbered SET job_number = issued;

                MERGE [serialisation].[number_series] WITH (HOLDLOCK) AS target
                USING (
                    SELECT tenant_id, 'PJ' AS series, YEAR(requested_at) AS [year], COUNT(*) AS last_value
                    FROM [serialisation].[print_job]
                    GROUP BY tenant_id, YEAR(requested_at)) AS source
                ON target.tenant_id = source.tenant_id AND target.series = source.series AND target.[year] = source.[year]
                WHEN MATCHED AND target.last_value < source.last_value THEN UPDATE SET last_value = source.last_value
                WHEN NOT MATCHED THEN INSERT (tenant_id, series, [year], last_value)
                    VALUES (source.tenant_id, source.series, source.[year], source.last_value);
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AlterColumn<string>(
                name: "job_number",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldUnicode: false,
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_print_job_tenant_id_job_number",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "job_number" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_template_form_type",
                schema: "serialisation",
                table: "certificate_template",
                sql: "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'warranty_certificate', 'custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_instance_form_type",
                schema: "serialisation",
                table: "certificate_instance",
                sql: "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'warranty_certificate', 'custom')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_print_job_tenant_id_job_number",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_template_form_type",
                schema: "serialisation",
                table: "certificate_template");

            migrationBuilder.DropCheckConstraint(
                name: "ck_certificate_instance_form_type",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.DropColumn(
                name: "job_number",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_template_form_type",
                schema: "serialisation",
                table: "certificate_template",
                sql: "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'custom')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_certificate_instance_form_type",
                schema: "serialisation",
                table: "certificate_instance",
                sql: "form_type IN ('generic_release', 'coc', 'dhr_extract', 'release_8130_3_style', 'easa_form_1_style', 'battery_passport', 'custom')");
        }
    }
}
