using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P6Labels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "label_template",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    create_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    applies_to = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    applies_to_value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_label_template", x => new { x.tenant_id, x.template_id });
                    table.CheckConstraint("ck_label_template_applies_to", "applies_to IN ('item_family', 'identifier_type')");
                });

            migrationBuilder.CreateTable(
                name: "label_template_version",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    effective_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    effective_to = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    layout_kind = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    layout = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    static_text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    bindings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    symbology = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    preview_sample_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    previewed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    previewed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    previewed_content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    legal_review = table.Column<bool>(type: "bit", nullable: false),
                    legal_review_requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    legal_review_requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    legal_reviewed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    legal_reviewed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    legal_review_content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    regulated = table.Column<bool>(type: "bit", nullable: false),
                    approved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    approved_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    approval_content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    content_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    command_ids = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    activated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    activated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    retired_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    retired_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_label_template_version", x => new { x.tenant_id, x.template_version_id });
                    table.CheckConstraint("ck_label_template_version_bindings_json", "ISJSON(bindings) = 1");
                    table.CheckConstraint("ck_label_template_version_command_ids_json", "ISJSON(command_ids) = 1");
                    table.CheckConstraint("ck_label_template_version_effective", "effective_to IS NULL OR effective_to > effective_from");
                    table.CheckConstraint("ck_label_template_version_layout_kind", "layout_kind IN ('zpl', 'html')");
                    table.CheckConstraint("ck_label_template_version_static_text_json", "ISJSON(static_text) = 1");
                    table.CheckConstraint("ck_label_template_version_status", "status IN ('draft', 'active', 'retired')");
                    table.CheckConstraint("ck_label_template_version_symbology", "symbology IN ('code128', 'datamatrix', 'qr', 'code39')");
                    table.ForeignKey(
                        name: "fk_label_template_version_tenant_id_template_id",
                        columns: x => new { x.tenant_id, x.template_id },
                        principalSchema: "serialisation",
                        principalTable: "label_template",
                        principalColumns: new[] { "tenant_id", "template_id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "label_template_version_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateTable(
                name: "print_job",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    identifier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    original_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    rendered_payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    payload_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    printer_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    requested_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    rejection_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    rendered_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    sent_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ledger_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_job", x => new { x.tenant_id, x.job_id });
                    table.CheckConstraint("ck_print_job_status", "status IN ('rendered', 'sent', 'confirmed', 'rejected')");
                    table.ForeignKey(
                        name: "fk_print_job_tenant_id_original_job_id",
                        columns: x => new { x.tenant_id, x.original_job_id },
                        principalSchema: "serialisation",
                        principalTable: "print_job",
                        principalColumns: new[] { "tenant_id", "job_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_print_job_tenant_id_template_version_id",
                        columns: x => new { x.tenant_id, x.template_version_id },
                        principalSchema: "serialisation",
                        principalTable: "label_template_version",
                        principalColumns: new[] { "tenant_id", "template_version_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_label_template_tenant_id_applies_to_applies_to_value",
                schema: "serialisation",
                table: "label_template",
                columns: new[] { "tenant_id", "applies_to", "applies_to_value" });

            migrationBuilder.CreateIndex(
                name: "ux_label_template_tenant_id_create_command_id",
                schema: "serialisation",
                table: "label_template",
                columns: new[] { "tenant_id", "create_command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_label_template_tenant_id_name",
                schema: "serialisation",
                table: "label_template",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_label_template_version_tenant_id_preview_sample_unit_id",
                schema: "serialisation",
                table: "label_template_version",
                columns: new[] { "tenant_id", "preview_sample_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_label_template_version_tenant_id_template_id_status_effective_from",
                schema: "serialisation",
                table: "label_template_version",
                columns: new[] { "tenant_id", "template_id", "status", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_label_template_version_tenant_id_template_id_version",
                schema: "serialisation",
                table: "label_template_version",
                columns: new[] { "tenant_id", "template_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_identifier_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "identifier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_original_job_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "original_job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_status_requested_at",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_template_version_id_requested_at",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "template_version_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_unit_id_requested_at",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "unit_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_print_job_tenant_id_command_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_print_job_tenant_id_source_event_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "source_event_id" },
                unique: true,
                filter: "[source_event_id] IS NOT NULL");

            migrationBuilder.AddTenantPolicy("serialisation", "label_template");
            migrationBuilder.AddTenantPolicy("serialisation", "label_template_version");
            migrationBuilder.AddTenantPolicy("serialisation", "print_job");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "print_job",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "label_template_version",
                schema: "serialisation")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "label_template_version_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "serialisation")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.DropTable(
                name: "label_template",
                schema: "serialisation");
        }
    }
}
