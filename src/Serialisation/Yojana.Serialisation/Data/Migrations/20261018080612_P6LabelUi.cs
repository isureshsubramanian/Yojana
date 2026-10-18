using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6LabelUi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "unit_id",
                schema: "serialisation",
                table: "print_job",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "batch_command_id",
                schema: "serialisation",
                table: "print_job",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "batch_sequence",
                schema: "serialisation",
                table: "print_job",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "empty_required_field",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_rule",
                schema: "serialisation",
                table: "print_job",
                type: "varchar(100)",
                unicode: false,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_serial",
                schema: "serialisation",
                table: "print_job",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_requested_at",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "requested_at" })
                .Annotation("SqlServer:Include", new[] { "status", "printer_ref", "original_job_id", "source_event_id" });

            migrationBuilder.CreateIndex(
                name: "ux_print_job_tenant_id_batch_command_id_batch_sequence",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "batch_command_id", "batch_sequence" },
                unique: true,
                filter: "[batch_command_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_print_job_tenant_id_requested_at",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropIndex(
                name: "ux_print_job_tenant_id_batch_command_id_batch_sequence",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "batch_command_id",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "batch_sequence",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "empty_required_field",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "rejection_rule",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropColumn(
                name: "requested_serial",
                schema: "serialisation",
                table: "print_job");

            // The earlier schema cannot represent an attempted batch row whose serial resolved to no unit. RLS hides
            // every tenant from a migration session, so the rollback removes only those rows with the policy off for
            // its own statement and immediately restores it.
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = OFF);");
            migrationBuilder.Sql("DELETE FROM [serialisation].[print_job] WHERE [unit_id] IS NULL;");
            migrationBuilder.Sql($"ALTER SECURITY POLICY [{TenantSecurityMigrations.SecuritySchema}].[{TenantSecurityMigrations.PolicyNameFor("serialisation")}] WITH (STATE = ON);");

            migrationBuilder.AlterColumn<Guid>(
                name: "unit_id",
                schema: "serialisation",
                table: "print_job",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
