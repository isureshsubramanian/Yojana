using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T12RetentionFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "question_attachment",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reply_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    question_body = table.Column<bool>(type: "bit", nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    content_type = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    content_length = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_attachment", x => new { x.tenant_id, x.file_id });
                    table.CheckConstraint("ck_question_attachment_target", "(question_body = 1 AND reply_id IS NULL) OR (question_body = 0 AND reply_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_question_attachment_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question_file_purge_pending",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    queued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    reason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_file_purge_pending", x => new { x.tenant_id, x.file_id });
                });

            migrationBuilder.CreateTable(
                name: "question_personal_data_redaction",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    redaction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    person_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    performed_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    performed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    scope_json = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    questions_redacted = table.Column<int>(type: "int", nullable: false),
                    replies_redacted = table.Column<int>(type: "int", nullable: false),
                    revisions_redacted = table.Column<int>(type: "int", nullable: false),
                    attachments_purged = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_personal_data_redaction", x => new { x.tenant_id, x.redaction_id });
                });

            migrationBuilder.CreateTable(
                name: "question_purge_run",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    triggered_by_kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    triggered_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    triggered_by_label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    cutoff = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    retention_years = table.Column<int>(type: "int", nullable: false),
                    questions_purged = table.Column<int>(type: "int", nullable: false),
                    replies_purged = table.Column<int>(type: "int", nullable: false),
                    revisions_purged = table.Column<int>(type: "int", nullable: false),
                    attachments_purged = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_purge_run", x => new { x.tenant_id, x.run_id });
                    table.CheckConstraint("ck_question_purge_run_trigger", "triggered_by_kind IN ('system','user')");
                    table.CheckConstraint("ck_question_purge_run_years", "retention_years BETWEEN 1 AND 100");
                });

            foreach (var table in new[] { "question_attachment", "question_file_purge_pending",
                         "question_personal_data_redaction", "question_purge_run" })
                migrationBuilder.AddTenantPolicy("knowledge", table);

            migrationBuilder.CreateIndex(
                name: "ix_question_attachment_tenant_id_question_id_reply_id_uploaded_at_file_id",
                schema: "knowledge",
                table: "question_attachment",
                columns: new[] { "tenant_id", "question_id", "reply_id", "uploaded_at", "file_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_file_purge_pending_tenant_id_queued_at_file_id",
                schema: "knowledge",
                table: "question_file_purge_pending",
                columns: new[] { "tenant_id", "queued_at", "file_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_personal_data_redaction_tenant_id_person_id_performed_at_redaction_id",
                schema: "knowledge",
                table: "question_personal_data_redaction",
                columns: new[] { "tenant_id", "person_id", "performed_at", "redaction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_purge_run_tenant_id_completed_at_run_id",
                schema: "knowledge",
                table: "question_purge_run",
                columns: new[] { "tenant_id", "completed_at", "run_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "question_attachment", "question_file_purge_pending",
                         "question_personal_data_redaction", "question_purge_run" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);

            migrationBuilder.DropTable(
                name: "question_attachment",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_file_purge_pending",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_personal_data_redaction",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_purge_run",
                schema: "knowledge");
        }
    }
}
