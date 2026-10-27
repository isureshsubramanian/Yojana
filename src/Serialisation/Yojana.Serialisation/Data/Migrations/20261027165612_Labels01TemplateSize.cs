using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Labels01TemplateSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_print_job_status",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.AddColumn<int>(
                name: "copies",
                schema: "serialisation",
                table: "print_job",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "downloaded_at",
                schema: "serialisation",
                table: "print_job",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "output_format",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reprint_reason",
                schema: "serialisation",
                table: "print_job",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sheet_profile_id",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_column",
                schema: "serialisation",
                table: "print_job",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_row",
                schema: "serialisation",
                table: "print_job",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "dpi_hint",
                schema: "serialisation",
                table: "label_template_version",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "height_mm",
                schema: "serialisation",
                table: "label_template_version",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "width_mm",
                schema: "serialisation",
                table: "label_template_version",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            // Label versions are a small administrator-owned catalogue. Disable RLS only around the bounded set-based
            // estimate backfill; the migration session otherwise cannot see every tenant's existing HTML versions.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                UPDATE [serialisation].[label_template_version]
                SET [width_mm] = 70.00, [height_mm] = 37.00
                WHERE [layout_kind] = 'html' AND ([width_mm] IS NULL OR [height_mm] IS NULL);
                """);
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_job_copies",
                schema: "serialisation",
                table: "print_job",
                sql: "copies BETWEEN 1 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_job_output_format",
                schema: "serialisation",
                table: "print_job",
                sql: "output_format IS NULL OR output_format IN ('sheet', 'one_per_page')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_job_status",
                schema: "serialisation",
                table: "print_job",
                sql: "status IN ('rendered', 'sent', 'confirmed', 'downloaded', 'rejected')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_label_template_version_dpi_hint",
                schema: "serialisation",
                table: "label_template_version",
                sql: "dpi_hint IS NULL OR dpi_hint BETWEEN 50 AND 1200");

            migrationBuilder.AddCheckConstraint(
                name: "ck_label_template_version_size",
                schema: "serialisation",
                table: "label_template_version",
                sql: "(layout_kind = 'html' AND width_mm BETWEEN 10 AND 200 AND height_mm BETWEEN 10 AND 200) OR (layout_kind = 'zpl' AND ((width_mm IS NULL AND height_mm IS NULL) OR (width_mm BETWEEN 10 AND 200 AND height_mm BETWEEN 10 AND 200)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_print_job_copies",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_print_job_output_format",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_print_job_status",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_label_template_version_dpi_hint",
                schema: "serialisation",
                table: "label_template_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_label_template_version_size",
                schema: "serialisation",
                table: "label_template_version");

            migrationBuilder.DropColumn(
                name: "copies",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "downloaded_at",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "output_format",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "reprint_reason",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "sheet_profile_id",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "start_column",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "start_row",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "dpi_hint",
                schema: "serialisation",
                table: "label_template_version");

            migrationBuilder.DropColumn(
                name: "height_mm",
                schema: "serialisation",
                table: "label_template_version");

            migrationBuilder.DropColumn(
                name: "width_mm",
                schema: "serialisation",
                table: "label_template_version");

            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("UPDATE [serialisation].[print_job] SET [status] = 'rendered' WHERE [status] = 'downloaded';");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_job_status",
                schema: "serialisation",
                table: "print_job",
                sql: "status IN ('rendered', 'sent', 'confirmed', 'rejected')");
        }
    }
}
