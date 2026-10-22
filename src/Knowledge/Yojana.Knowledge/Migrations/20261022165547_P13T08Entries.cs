using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T08Entries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_entry",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    problem = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    answer = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    steps = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    written_against_json = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source_question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    current_version = table.Column<int>(type: "int", nullable: false),
                    author_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_saved_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    last_saved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    published_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    published_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    retired_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    retired_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    retirement_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry", x => new { x.tenant_id, x.entry_id });
                    table.CheckConstraint("ck_knowledge_entry_published_state", "(status IN ('published','retired') AND published_by IS NOT NULL AND published_at IS NOT NULL) OR status NOT IN ('published','retired')");
                    table.CheckConstraint("ck_knowledge_entry_retired_state", "(status = 'retired' AND retired_by IS NOT NULL AND retired_at IS NOT NULL AND retirement_note IS NOT NULL) OR status <> 'retired'");
                    table.CheckConstraint("ck_knowledge_entry_status", "status IN ('draft','in_review','published','retired')");
                    table.CheckConstraint("ck_knowledge_entry_version", "current_version >= 1");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_tenant_id_source_question_id",
                        columns: x => new { x.tenant_id, x.source_question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_entry_version",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    problem = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    answer = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    steps = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    written_against_json = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    saved_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    saved_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_version", x => new { x.tenant_id, x.entry_id, x.version });
                    table.CheckConstraint("ck_knowledge_entry_version_number", "version >= 1");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_version_tenant_id_entry_id",
                        columns: x => new { x.tenant_id, x.entry_id },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry",
                        principalColumns: new[] { "tenant_id", "entry_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_entry_record_link",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    record_kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_record_link", x => new { x.tenant_id, x.entry_id, x.version, x.record_kind, x.record_id });
                    table.ForeignKey(
                        name: "fk_knowledge_entry_record_link_tenant_id_entry_id_version",
                        columns: x => new { x.tenant_id, x.entry_id, x.version },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry_version",
                        principalColumns: new[] { "tenant_id", "entry_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_entry_review_round",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    round = table.Column<int>(type: "int", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    decision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    reviewer = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_review_round", x => new { x.tenant_id, x.entry_id, x.round });
                    table.CheckConstraint("ck_knowledge_entry_review_decision", "decision IS NULL OR decision IN ('approve','send_back')");
                    table.CheckConstraint("ck_knowledge_entry_review_round_number", "round >= 1");
                    table.CheckConstraint("ck_knowledge_entry_review_state", "(decision IS NULL AND reviewer IS NULL AND reviewed_at IS NULL) OR (decision IS NOT NULL AND reviewer IS NOT NULL AND reviewed_at IS NOT NULL)");
                    table.CheckConstraint("ck_knowledge_entry_send_back_note", "decision <> 'send_back' OR note IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_review_round_tenant_id_entry_id_version",
                        columns: x => new { x.tenant_id, x.entry_id, x.version },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry_version",
                        principalColumns: new[] { "tenant_id", "entry_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_entry_tag",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    label = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_tag", x => new { x.tenant_id, x.entry_id, x.version, x.kind, x.value });
                    table.CheckConstraint("ck_knowledge_entry_tag_kind", "kind IN ('role','task','item_family','claim_cause')");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_tag_tenant_id_entry_id_version",
                        columns: x => new { x.tenant_id, x.entry_id, x.version },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry_version",
                        principalColumns: new[] { "tenant_id", "entry_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_tenant_id_status_last_saved_at_entry_id",
                schema: "knowledge",
                table: "knowledge_entry",
                columns: new[] { "tenant_id", "status", "last_saved_at", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_entry_tenant_id_source_question_id",
                schema: "knowledge",
                table: "knowledge_entry",
                columns: new[] { "tenant_id", "source_question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_record_link_tenant_id_record_kind_record_id_entry_id_version",
                schema: "knowledge",
                table: "knowledge_entry_record_link",
                columns: new[] { "tenant_id", "record_kind", "record_id", "entry_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_review_round_tenant_id_decision_submitted_at_entry_id_round",
                schema: "knowledge",
                table: "knowledge_entry_review_round",
                columns: new[] { "tenant_id", "decision", "submitted_at", "entry_id", "round" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_review_round_tenant_id_entry_id_version",
                schema: "knowledge",
                table: "knowledge_entry_review_round",
                columns: new[] { "tenant_id", "entry_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_tag_tenant_id_kind_value_entry_id_version",
                schema: "knowledge",
                table: "knowledge_entry_tag",
                columns: new[] { "tenant_id", "kind", "value", "entry_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_version_tenant_id_entry_id_saved_at",
                schema: "knowledge",
                table: "knowledge_entry_version",
                columns: new[] { "tenant_id", "entry_id", "saved_at" });

            foreach (var table in new[] { "knowledge_entry", "knowledge_entry_version", "knowledge_entry_tag",
                         "knowledge_entry_record_link", "knowledge_entry_review_round" })
                migrationBuilder.AddTenantPolicy("knowledge", table);

            // SQL Server full-text keys must be unique single-column indexes. This is the same narrow exception used
            // for questions: tenant isolation remains in the composite key and RLS policy, and no ordinary query uses it.
            migrationBuilder.Sql("CREATE UNIQUE INDEX [ux_knowledge_entry_entry_id] ON [knowledge].[knowledge_entry] ([entry_id])");
            // Coupled to SqlServerFullTextQuestionSearch.SearchEntriesAsync. Renaming this key or the index there
            // without rebuilding this full-text index makes entry search unavailable.
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'knowledge_questions')
                        EXEC(N'CREATE FULLTEXT CATALOG [knowledge_questions] WITH ACCENT_SENSITIVITY = OFF');
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[knowledge_entry]'))
                        EXEC(N'CREATE FULLTEXT INDEX ON [knowledge].[knowledge_entry]
                            ([title] LANGUAGE 1033, [problem] LANGUAGE 1033, [answer] LANGUAGE 1033, [steps] LANGUAGE 1033)
                            KEY INDEX [ux_knowledge_entry_entry_id] ON [knowledge_questions] WITH CHANGE_TRACKING AUTO');
                END
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                    AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[knowledge_entry]'))
                    EXEC(N'DROP FULLTEXT INDEX ON [knowledge].[knowledge_entry]');
                """, suppressTransaction: true);
            migrationBuilder.DropIndex(
                name: "ux_knowledge_entry_entry_id",
                schema: "knowledge",
                table: "knowledge_entry");

            foreach (var table in new[] { "knowledge_entry_review_round", "knowledge_entry_record_link",
                         "knowledge_entry_tag", "knowledge_entry_version", "knowledge_entry" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);

            migrationBuilder.DropTable(
                name: "knowledge_entry_record_link",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_entry_review_round",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_entry_tag",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_entry_version",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_entry",
                schema: "knowledge");
        }
    }
}
