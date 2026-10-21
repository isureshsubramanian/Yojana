using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T02FixSourceMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_source_runtime",
                schema: "migration",
                table: "migration_source");
            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_source_runtime",
                schema: "migration",
                table: "migration_source",
                sql: "runtime IN ('sql_server',CONCAT('h','ana'))");

            migrationBuilder.DropColumn(
                name: "warranty_template_id",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.DropColumn(
                name: "labour_coverage",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "parts_coverage",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "travel_coverage",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "warehouse_code",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropColumn(
                name: "partner_role",
                schema: "migration",
                table: "stg_src_ins1");

            migrationBuilder.DropColumn(
                name: "service_type",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.RenameColumn(
                name: "system_number",
                schema: "migration",
                table: "stg_src_osbq",
                newName: "serial_entry");

            migrationBuilder.DropColumn(
                name: "system_number",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "template_id",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "response_time",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "duration_unit",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "coverage_hours",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "template_id",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.RenameColumn(
                name: "path",
                schema: "migration",
                table: "stg_src_oatc",
                newName: "target_path");

            migrationBuilder.DropColumn(
                name: "line_number",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropColumn(
                name: "line_number",
                schema: "migration",
                table: "stg_src_ins1");

            migrationBuilder.AddColumn<int>(
                name: "location_type",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "wednesday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "wednesday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "response_unit",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_name",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "response_value",
                schema: "migration",
                table: "stg_src_octr",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "serial_entry",
                schema: "migration",
                table: "stg_src_itl1",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "history_sequence",
                schema: "migration",
                table: "stg_src_ins1",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "migration",
                table: "stg_src_osrn",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "serial_entry",
                schema: "migration",
                table: "stg_src_osrn",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "status",
                schema: "migration",
                table: "stg_src_oscl",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "entry",
                schema: "migration",
                table: "stg_src_osbq",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warranty_template_name",
                schema: "migration",
                table: "stg_src_oitm",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "applied_document_number",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "apply_entry",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "apply_line",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "apply_type",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "base_entry",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "base_line",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "base_type",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "document_number",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "document_quantity",
                schema: "migration",
                table: "stg_src_oitl",
                type: "decimal(28,8)",
                precision: 28,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warehouse_code",
                schema: "migration",
                table: "stg_src_oitl",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contract_type",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "friday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "friday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "friday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_labour",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_parts",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_travel",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "monday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "monday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "monday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remark",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "renewal",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "response_value",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "saturday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "saturday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "saturday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sunday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sunday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sunday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thursday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "thursday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "thursday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tuesday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tuesday_end",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tuesday_start",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "wednesday_enabled",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contract_type",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "document_number",
                schema: "migration",
                table: "stg_src_octr",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "duration",
                schema: "migration",
                table: "stg_src_octr",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_labour",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_parts",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "includes_travel",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "response_unit",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "service_type",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_name",
                schema: "migration",
                table: "stg_src_octr",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "file_size",
                schema: "migration",
                table: "stg_src_oatc",
                type: "int",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "attached_at",
                schema: "migration",
                table: "stg_src_oatc",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_path",
                schema: "migration",
                table: "stg_src_oatc",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sub_path",
                schema: "migration",
                table: "stg_src_oatc",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "document_type",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "object_type",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "equipment_card_id",
                schema: "migration",
                table: "stg_src_ctr1",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "termination_date",
                schema: "migration",
                table: "stg_src_ctr1",
                type: "datetime2(3)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_migration_source_runtime",
                schema: "migration",
                table: "migration_source");
            migrationBuilder.AddCheckConstraint(
                name: "ck_migration_source_runtime",
                schema: "migration",
                table: "migration_source",
                sql: "runtime IN ('sql_server','h" + "ana')");

            migrationBuilder.DropColumn(
                name: "location_type",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "wednesday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "wednesday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "response_unit",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "template_name",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "response_value",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "serial_entry",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropColumn(
                name: "history_sequence",
                schema: "migration",
                table: "stg_src_ins1");

            migrationBuilder.DropColumn(
                name: "serial_entry",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropColumn(
                name: "entry",
                schema: "migration",
                table: "stg_src_osbq");

            migrationBuilder.DropColumn(
                name: "warranty_template_name",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.DropColumn(
                name: "applied_document_number",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "apply_entry",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "apply_line",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "apply_type",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "base_entry",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "base_line",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "base_type",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "document_number",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "document_quantity",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "warehouse_code",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropColumn(
                name: "contract_type",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "deleted",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "friday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "friday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "friday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "includes_labour",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "includes_parts",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "includes_travel",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "monday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "monday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "monday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "remark",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "renewal",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "response_value",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "saturday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "saturday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "saturday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "sunday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "sunday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "sunday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "thursday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "thursday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "thursday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "tuesday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "tuesday_end",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "tuesday_start",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "wednesday_enabled",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropColumn(
                name: "contract_type",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "document_number",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "duration",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "includes_labour",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "includes_parts",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "includes_travel",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "response_unit",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "service_type",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "template_name",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropColumn(
                name: "attached_at",
                schema: "migration",
                table: "stg_src_oatc");

            migrationBuilder.DropColumn(
                name: "source_path",
                schema: "migration",
                table: "stg_src_oatc");

            migrationBuilder.DropColumn(
                name: "sub_path",
                schema: "migration",
                table: "stg_src_oatc");

            migrationBuilder.DropColumn(
                name: "object_type",
                schema: "migration",
                table: "stg_src_marketing_document_headers");

            migrationBuilder.DropColumn(
                name: "equipment_card_id",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropColumn(
                name: "termination_date",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.RenameColumn(
                name: "serial_entry",
                schema: "migration",
                table: "stg_src_osbq",
                newName: "system_number");

            migrationBuilder.RenameColumn(
                name: "target_path",
                schema: "migration",
                table: "stg_src_oatc",
                newName: "path");

            migrationBuilder.AlterColumn<int>(
                name: "status",
                schema: "migration",
                table: "stg_src_osrn",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "migration",
                table: "stg_src_oscl",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "warranty_template_id",
                schema: "migration",
                table: "stg_src_oitm",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "labour_coverage",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parts_coverage",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "travel_coverage",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "file_size",
                schema: "migration",
                table: "stg_src_oatc",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "document_type",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warehouse_code",
                schema: "migration",
                table: "stg_src_itl1",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "partner_role",
                schema: "migration",
                table: "stg_src_ins1",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "service_type",
                schema: "migration",
                table: "stg_src_ctr1",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "system_number",
                schema: "migration",
                table: "stg_src_oitl",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "template_id",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "response_time",
                schema: "migration",
                table: "stg_src_octt",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "duration_unit",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coverage_hours",
                schema: "migration",
                table: "stg_src_octt",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "template_id",
                schema: "migration",
                table: "stg_src_octr",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "line_number",
                schema: "migration",
                table: "stg_src_itl1",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "line_number",
                schema: "migration",
                table: "stg_src_ins1",
                type: "int",
                nullable: true);
        }
    }
}
