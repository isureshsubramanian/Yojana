using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <summary>
    /// P9-T20 customer e-mail: templates, their versions and per-language defaults, and the sent log with each message
    /// as it was sent. Every table is tenant-led and under the tenant row-level security policy.
    /// </summary>
    public partial class P9T20CustomerMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_mail_message",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_version = table.Column<int>(type: "int", nullable: false),
                    template_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    language = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    about_label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    customer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    customer_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    to_address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    to_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    cc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    html_body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    text_body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    attachment_name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    has_note = table.Column<bool>(type: "bit", nullable: false),
                    sent_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    copy_to_self = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    queued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    sent_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    transport = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_mail_message", x => new { x.tenant_id, x.message_id });
                    table.CheckConstraint("ck_customer_mail_message_cc", "ISJSON(cc) = 1");
                    table.CheckConstraint("ck_customer_mail_message_status", "status IN ('queued','retrying','sent','failed')");
                });

            migrationBuilder.CreateTable(
                name: "mail_template",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    started_from = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_template", x => new { x.tenant_id, x.template_id });
                });

            migrationBuilder.CreateTable(
                name: "mail_template_default",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    language = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_template_default", x => new { x.tenant_id, x.kind, x.language });
                    table.ForeignKey(
                        name: "fk_mail_template_default_tenant_id_template_id",
                        columns: x => new { x.tenant_id, x.template_id },
                        principalSchema: "serialisation",
                        principalTable: "mail_template",
                        principalColumns: new[] { "tenant_id", "template_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_template_version",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    template_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    submitted_for_review = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    published_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    published_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    retired_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    retired_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_template_version", x => new { x.tenant_id, x.version_id });
                    table.CheckConstraint("ck_mail_template_version_content", "ISJSON(content) = 1");
                    table.CheckConstraint("ck_mail_template_version_status", "status IN ('draft','published','retired')");
                    table.ForeignKey(
                        name: "fk_mail_template_version_tenant_id_template_id",
                        columns: x => new { x.tenant_id, x.template_id },
                        principalSchema: "serialisation",
                        principalTable: "mail_template",
                        principalColumns: new[] { "tenant_id", "template_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_command_id",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_queued_at_message_id",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "queued_at", "message_id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_status_queued_at",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "status", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_template_version_id",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "template_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_unit_id_queued_at",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "unit_id", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_template_tenant_id_kind_name",
                schema: "serialisation",
                table: "mail_template",
                columns: new[] { "tenant_id", "kind", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_template_default_tenant_id_template_id",
                schema: "serialisation",
                table: "mail_template_default",
                columns: new[] { "tenant_id", "template_id" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_template_version_tenant_id_template_id_version",
                schema: "serialisation",
                table: "mail_template_version",
                columns: new[] { "tenant_id", "template_id", "version" },
                unique: true);
            migrationBuilder.AddTenantPolicy("serialisation", "mail_template");
            migrationBuilder.AddTenantPolicy("serialisation", "mail_template_version");
            migrationBuilder.AddTenantPolicy("serialisation", "mail_template_default");
            migrationBuilder.AddTenantPolicy("serialisation", "customer_mail_message");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "customer_mail_message");
            migrationBuilder.RemoveTenantPolicy("serialisation", "mail_template_default");
            migrationBuilder.RemoveTenantPolicy("serialisation", "mail_template_version");
            migrationBuilder.RemoveTenantPolicy("serialisation", "mail_template");
            migrationBuilder.DropTable(
                name: "customer_mail_message",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "mail_template_default",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "mail_template_version",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "mail_template",
                schema: "serialisation");
        }
    }
}
