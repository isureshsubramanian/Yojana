using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T02MigrationStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "migration");

            migrationBuilder.CreateTable(
                name: "migration_source",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    display_label = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    runtime = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    route = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    database_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    schema_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    linked_server_name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    command_timeout_seconds = table.Column<int>(type: "int", nullable: false),
                    licence_position = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    licence_confirmed_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    licence_confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    licence_contract_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    licence_note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_source", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_migration_source_licence", "licence_position IN ('unconfirmed','confirmed')");
                    table.CheckConstraint("ck_migration_source_route", "route IN ('separate','cross_database','linked_server')");
                    table.CheckConstraint("ck_migration_source_runtime", "runtime IN ('sql_server','h" + "ana')");
                    table.CheckConstraint("ck_migration_source_timeout", "command_timeout_seconds BETWEEN 1 AND 3600");
                });

            migrationBuilder.CreateTable(
                name: "stg_src_ains",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    card_id = table.Column<int>(type: "int", nullable: true),
                    history_sequence = table.Column<int>(type: "int", nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    warranty_start = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    warranty_end = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    changed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_ains", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_ctr1",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    contract_id = table.Column<int>(type: "int", nullable: true),
                    line_number = table.Column<int>(type: "int", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    internal_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    manufacturer_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    service_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    start_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    end_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_ctr1", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_ins1",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    card_id = table.Column<int>(type: "int", nullable: true),
                    line_number = table.Column<int>(type: "int", nullable: true),
                    partner_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    partner_role = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_ins1", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_itl1",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    log_entry = table.Column<int>(type: "int", nullable: true),
                    line_number = table.Column<int>(type: "int", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    system_number = table.Column<int>(type: "int", nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    warehouse_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_itl1", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_marketing_document_headers",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    source_document_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    document_type = table.Column<int>(type: "int", nullable: true),
                    document_entry = table.Column<int>(type: "int", nullable: true),
                    document_number = table.Column<int>(type: "int", nullable: true),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    document_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    cancelled = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_marketing_document_headers", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_oatc",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    attachment_entry = table.Column<int>(type: "int", nullable: true),
                    line_number = table.Column<int>(type: "int", nullable: true),
                    path = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    file_name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    file_extension = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_oatc", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_octr",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    contract_id = table.Column<int>(type: "int", nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    start_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    end_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    termination_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    template_id = table.Column<int>(type: "int", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_octr", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_octt",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    template_id = table.Column<int>(type: "int", nullable: true),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    duration = table.Column<int>(type: "int", nullable: true),
                    duration_unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    parts_coverage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    labour_coverage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    travel_coverage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    response_time = table.Column<int>(type: "int", nullable: true),
                    coverage_hours = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_octt", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_oins",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    card_id = table.Column<int>(type: "int", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    internal_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    manufacturer_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    direct_customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    replaces_card_id = table.Column<int>(type: "int", nullable: true),
                    replaced_by_card_id = table.Column<int>(type: "int", nullable: true),
                    personal_data_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    warranty_start = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    warranty_end = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_oins", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_oitl",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    log_entry = table.Column<int>(type: "int", nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    system_number = table.Column<int>(type: "int", nullable: true),
                    document_type = table.Column<int>(type: "int", nullable: true),
                    document_entry = table.Column<int>(type: "int", nullable: true),
                    document_line = table.Column<int>(type: "int", nullable: true),
                    document_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_time = table.Column<int>(type: "int", nullable: true),
                    stock_effect = table.Column<int>(type: "int", nullable: true),
                    party_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_oitl", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_oitm",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    manages_serial_numbers = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    management_method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    warranty_template_id = table.Column<int>(type: "int", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_oitm", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_orrr",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    document_entry = table.Column<int>(type: "int", nullable: true),
                    document_number = table.Column<int>(type: "int", nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    document_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    cancelled = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_orrr", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_osbq",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    system_number = table.Column<int>(type: "int", nullable: true),
                    warehouse_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    bin_entry = table.Column<int>(type: "int", nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_osbq", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_oscl",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    call_id = table.Column<int>(type: "int", nullable: true),
                    customer_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    internal_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    manufacturer_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    opened_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_oscl", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_osrn",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    system_number = table.Column<int>(type: "int", nullable: true),
                    serial_number = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    manufacturer_serial = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    lot_number = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    expiry_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    manufacture_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    inbound_date = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    warranty_start = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    warranty_end = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<int>(type: "int", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    data_source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_osrn", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "stg_src_osrq",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    source_pk = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    system_number = table.Column<int>(type: "int", nullable: true),
                    warehouse_code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extracted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    row_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    first_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absent_since_batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stg_src_osrq", x => new { x.tenant_id, x.source_system, x.source_table, x.source_pk });
                });

            migrationBuilder.CreateTable(
                name: "migration_batch",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_system = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    requested_by = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    finished_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    failure_text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_batch", x => new { x.tenant_id, x.batch_id });
                    table.CheckConstraint("ck_migration_batch_kind", "kind = 'full'");
                    table.CheckConstraint("ck_migration_batch_status", "status IN ('queued','running','succeeded','failed','refused')");
                    table.ForeignKey(
                        name: "fk_migration_batch_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "migration",
                        principalTable: "migration_source",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "migration_batch_table",
                schema: "migration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_table = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    rows_read = table.Column<long>(type: "bigint", nullable: false),
                    inserted = table.Column<long>(type: "bigint", nullable: false),
                    updated = table.Column<long>(type: "bigint", nullable: false),
                    unchanged = table.Column<long>(type: "bigint", nullable: false),
                    absent = table.Column<long>(type: "bigint", nullable: false),
                    duration_milliseconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_migration_batch_table", x => new { x.tenant_id, x.batch_id, x.source_table });
                    table.ForeignKey(
                        name: "fk_migration_batch_table_tenant_id_batch_id",
                        columns: x => new { x.tenant_id, x.batch_id },
                        principalSchema: "migration",
                        principalTable: "migration_batch",
                        principalColumns: new[] { "tenant_id", "batch_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_batch_tenant_id_connection_id",
                schema: "migration",
                table: "migration_batch",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_batch_tenant_id_requested_at_batch_id",
                schema: "migration",
                table: "migration_batch",
                columns: new[] { "tenant_id", "requested_at", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_batch_tenant_id_status_requested_at_batch_id",
                schema: "migration",
                table: "migration_batch",
                columns: new[] { "tenant_id", "status", "requested_at", "batch_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_batch_tenant_id_source_system",
                schema: "migration",
                table: "migration_batch",
                columns: new[] { "tenant_id", "source_system" },
                unique: true,
                filter: "[status] = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_migration_batch_table_tenant_id_batch_id_source_table",
                schema: "migration",
                table: "migration_batch_table",
                columns: new[] { "tenant_id", "batch_id", "source_table" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_source_tenant_id_created_at_connection_id",
                schema: "migration",
                table: "migration_source",
                columns: new[] { "tenant_id", "created_at", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ux_migration_source_tenant_id_source_system",
                schema: "migration",
                table: "migration_source",
                columns: new[] { "tenant_id", "source_system" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ains_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ains_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ins1_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ins1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_itl1_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oatc_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oatc_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octr_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octt_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octt_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitm_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_orrr_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_orrr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osbq_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osbq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oscl_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oscl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrq_tenant_id_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq",
                columns: new[] { "tenant_id", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            foreach (var table in new[]
            {
                "migration_source", "migration_batch", "migration_batch_table",
                "stg_src_osrn", "stg_src_osrq", "stg_src_osbq", "stg_src_oitl", "stg_src_itl1",
                "stg_src_oins", "stg_src_ains", "stg_src_ins1", "stg_src_octr", "stg_src_ctr1",
                "stg_src_octt", "stg_src_oscl", "stg_src_orrr", "stg_src_marketing_document_headers",
                "stg_src_oatc", "stg_src_oitm"
            })
                migrationBuilder.AddTenantPolicy("migration", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
            {
                "migration_batch_table", "migration_batch", "migration_source",
                "stg_src_osrn", "stg_src_osrq", "stg_src_osbq", "stg_src_oitl", "stg_src_itl1",
                "stg_src_oins", "stg_src_ains", "stg_src_ins1", "stg_src_octr", "stg_src_ctr1",
                "stg_src_octt", "stg_src_oscl", "stg_src_orrr", "stg_src_marketing_document_headers",
                "stg_src_oatc", "stg_src_oitm"
            })
                migrationBuilder.RemoveTenantPolicy("migration", table);

            migrationBuilder.DropTable(
                name: "migration_batch_table",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_ains",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_ctr1",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_ins1",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_itl1",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_marketing_document_headers",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_oatc",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_octr",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_octt",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_oins",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_oitl",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_oitm",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_orrr",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_osbq",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_oscl",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_osrn",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "stg_src_osrq",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_batch",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "migration_source",
                schema: "migration");
        }
    }
}
