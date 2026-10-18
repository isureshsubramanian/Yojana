using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P7T02Imports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "column_mapping",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    mapping_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    mapping_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    is_default = table.Column<bool>(type: "bit", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_column_mapping", x => new { x.tenant_id, x.mapping_id });
                    table.CheckConstraint("ck_column_mapping_json", "ISJSON(mapping_json) = 1");
                });

            migrationBuilder.CreateTable(
                name: "import_job",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    original_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    converted_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    format = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    mode = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    target_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    target_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    target_line = table.Column<int>(type: "int", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    applied_count = table.Column<int>(type: "int", nullable: false),
                    skipped_count = table.Column<int>(type: "int", nullable: false),
                    rejected_count = table.Column<int>(type: "int", nullable: false),
                    original_archive_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    converted_archive_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    inbox_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    original_content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    converted_content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_time = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_job", x => new { x.tenant_id, x.job_id });
                    table.CheckConstraint("ck_import_job_mode", "mode IN ('apply', 'compare')");
                    table.CheckConstraint("ck_import_job_status", "status IN ('intake', 'applied', 'partial', 'rejected', 'compared')");
                });

            migrationBuilder.CreateTable(
                name: "import_row_result",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    row_result_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    row_number = table.Column<int>(type: "int", nullable: false),
                    sheet = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    raw_value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    normalised_value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    rule = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    order_number = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    lot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    customer_asset_tag = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    collision_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    collision_owner_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    comparison_partition = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    proposed_action = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    reviewed_action = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_row_result", x => new { x.tenant_id, x.row_result_id });
                    table.CheckConstraint("ck_import_row_result_outcome", "outcome IN ('applied', 'skipped', 'rejected')");
                    table.ForeignKey(
                        name: "fk_import_row_result_tenant_id_job_id",
                        columns: x => new { x.tenant_id, x.job_id },
                        principalSchema: "serialisation",
                        principalTable: "import_job",
                        principalColumns: new[] { "tenant_id", "job_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "three_pl_feed_message",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    feed_message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    document_type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_line = table.Column<int>(type: "int", nullable: false),
                    import_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payload_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    shipped_count = table.Column<int>(type: "int", nullable: false),
                    asset_tag_count = table.Column<int>(type: "int", nullable: false),
                    rejected_count = table.Column<int>(type: "int", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    recorded_time = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_three_pl_feed_message", x => new { x.tenant_id, x.feed_message_id });
                    table.ForeignKey(
                        name: "fk_three_pl_feed_message_tenant_id_import_job_id",
                        columns: x => new { x.tenant_id, x.import_job_id },
                        principalSchema: "serialisation",
                        principalTable: "import_job",
                        principalColumns: new[] { "tenant_id", "job_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_column_mapping_tenant_id_source_is_default",
                schema: "serialisation",
                table: "column_mapping",
                columns: new[] { "tenant_id", "source", "is_default" });

            migrationBuilder.CreateIndex(
                name: "ux_column_mapping_tenant_id_source_name",
                schema: "serialisation",
                table: "column_mapping",
                columns: new[] { "tenant_id", "source", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_source_original_hash",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "source", "original_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_status_recorded_time",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "status", "recorded_time" });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_target_type_target_id_target_line",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "target_type", "target_id", "target_line" });

            migrationBuilder.CreateIndex(
                name: "ux_import_job_tenant_id_command_id",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_row_result_tenant_id_job_id_row_number_row_result_id",
                schema: "serialisation",
                table: "import_row_result",
                columns: new[] { "tenant_id", "job_id", "row_number", "row_result_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_row_result_tenant_id_normalised_value_job_id",
                schema: "serialisation",
                table: "import_row_result",
                columns: new[] { "tenant_id", "normalised_value", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_row_result_tenant_id_unit_id_job_id",
                schema: "serialisation",
                table: "import_row_result",
                columns: new[] { "tenant_id", "unit_id", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_three_pl_feed_message_tenant_id_document_type_document_id_document_line",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "document_type", "document_id", "document_line" });

            migrationBuilder.CreateIndex(
                name: "ix_three_pl_feed_message_tenant_id_import_job_id",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "import_job_id" });

            migrationBuilder.CreateIndex(
                name: "ux_three_pl_feed_message_tenant_id_command_id",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_three_pl_feed_message_tenant_id_message_reference",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "message_reference" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "import_job");
            migrationBuilder.AddTenantPolicy("serialisation", "import_row_result");
            migrationBuilder.AddTenantPolicy("serialisation", "column_mapping");
            migrationBuilder.AddTenantPolicy("serialisation", "three_pl_feed_message");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "column_mapping",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "import_row_result",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "three_pl_feed_message",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "import_job",
                schema: "serialisation");
        }
    }
}
