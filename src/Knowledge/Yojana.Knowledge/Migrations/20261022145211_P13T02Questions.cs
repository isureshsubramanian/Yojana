using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T02Questions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.CreateTable(
                name: "question",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    record_kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    directed_to_user = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    directed_to_role = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    asked_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    accepted_reply_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closed_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    closed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    closed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    reopened_count = table.Column<int>(type: "int", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question", x => new { x.tenant_id, x.question_id });
                    table.CheckConstraint("ck_question_closed_state", "(status = 'closed_without_answer' AND closed_reason IS NOT NULL AND closed_by IS NOT NULL AND closed_at IS NOT NULL) OR status <> 'closed_without_answer'");
                    table.CheckConstraint("ck_question_directed_exactly_one", "(directed_to_user IS NOT NULL AND directed_to_role IS NULL) OR (directed_to_user IS NULL AND directed_to_role IS NOT NULL)");
                    table.CheckConstraint("ck_question_status", "status IN ('open','answered','closed_without_answer')");
                });

            migrationBuilder.CreateTable(
                name: "question_participant",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asked = table.Column<bool>(type: "bit", nullable: false),
                    directed = table.Column<bool>(type: "bit", nullable: false),
                    replied = table.Column<bool>(type: "bit", nullable: false),
                    last_read_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_participant", x => new { x.tenant_id, x.question_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_question_participant_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question_reply",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reply_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    author = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    edited_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_reply", x => new { x.tenant_id, x.reply_id });
                    table.ForeignKey(
                        name: "fk_question_reply_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question_reply_revision",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    revision_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reply_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    question_body = table.Column<bool>(type: "bit", nullable: false),
                    body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    revised_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    revised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_reply_revision", x => new { x.tenant_id, x.revision_id });
                    table.CheckConstraint("ck_question_reply_revision_target", "(question_body = 1 AND reply_id IS NULL) OR (question_body = 0 AND reply_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_question_reply_revision_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_asked_by_status_asked_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "asked_by", "status", "asked_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_directed_to_role_status_asked_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "directed_to_role", "status", "asked_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_directed_to_user_status_asked_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "directed_to_user", "status", "asked_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_record_kind_record_id_status_asked_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "record_kind", "record_id", "status", "asked_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_participant_tenant_id_user_id_last_read_at_question_id",
                schema: "knowledge",
                table: "question_participant",
                columns: new[] { "tenant_id", "user_id", "last_read_at", "question_id" });

            migrationBuilder.CreateIndex(
                name: "ux_question_reply_tenant_id_question_id_at_reply_id",
                schema: "knowledge",
                table: "question_reply",
                columns: new[] { "tenant_id", "question_id", "at", "reply_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_question_reply_revision_tenant_id_question_id_reply_id_revised_at_revision_id",
                schema: "knowledge",
                table: "question_reply_revision",
                columns: new[] { "tenant_id", "question_id", "reply_id", "revised_at", "revision_id" });

            foreach (var table in new[] { "question", "question_reply", "question_reply_revision", "question_participant" })
                migrationBuilder.AddTenantPolicy("knowledge", table);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "question_participant", "question_reply_revision", "question_reply", "question" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'tenant_policy_knowledge') DROP SECURITY POLICY [sec].[tenant_policy_knowledge];");

            migrationBuilder.DropTable(
                name: "question_participant",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_reply",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question_reply_revision",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "question",
                schema: "knowledge");
        }
    }
}
