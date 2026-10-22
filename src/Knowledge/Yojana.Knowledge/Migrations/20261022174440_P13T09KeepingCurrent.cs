using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T09KeepingCurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "question_id",
                schema: "knowledge",
                table: "question_notification",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "entry_id",
                schema: "knowledge",
                table: "question_notification",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "needs_review",
                schema: "knowledge",
                table: "knowledge_entry",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "needs_review_since",
                schema: "knowledge",
                table: "knowledge_entry",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "published_version",
                schema: "knowledge",
                table: "knowledge_entry",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_entry_review_reason",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    reason_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    label = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    trigger_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    flagged_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    cleared_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    cleared_at_version = table.Column<int>(type: "int", nullable: true),
                    clearance = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_review_reason", x => new { x.tenant_id, x.reason_id });
                    table.CheckConstraint("ck_knowledge_entry_review_reason_clearance", "(cleared_at IS NULL AND cleared_by IS NULL AND cleared_at_version IS NULL AND clearance IS NULL) OR (cleared_at IS NOT NULL AND cleared_by IS NOT NULL AND cleared_at_version IS NOT NULL AND clearance IS NOT NULL)");
                    table.CheckConstraint("ck_knowledge_entry_review_reason_kind", "kind IN ('policy_version_superseded','screen_changed')");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_review_reason_tenant_id_entry_id",
                        columns: x => new { x.tenant_id, x.entry_id },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry",
                        principalColumns: new[] { "tenant_id", "entry_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_entry_written_against",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    label = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_entry_written_against", x => new { x.tenant_id, x.entry_id, x.version, x.kind, x.reference });
                    table.CheckConstraint("ck_knowledge_entry_written_against_kind", "kind IN ('policy_version','screen')");
                    table.ForeignKey(
                        name: "fk_knowledge_entry_written_against_tenant_id_entry_id_version",
                        columns: x => new { x.tenant_id, x.entry_id, x.version },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry_version",
                        principalColumns: new[] { "tenant_id", "entry_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_screen_change",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    change_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    screen_reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    screen_label = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    changed_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    changed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_screen_change", x => new { x.tenant_id, x.change_id });
                });

            // Existing reviewed entries remain readable while the new currentness model is introduced. The JSON was
            // validated by T08; OPENJSON turns it into indexed dependency records in one set-based operation.
            migrationBuilder.Sql("""
                UPDATE [knowledge].[knowledge_entry]
                SET [published_version] = [current_version]
                WHERE [status] IN ('published', 'retired');

                INSERT INTO [knowledge].[knowledge_entry_written_against]
                    ([tenant_id], [entry_id], [version], [kind], [reference], [label])
                SELECT [entry].[tenant_id], [entry].[entry_id], [entry].[current_version],
                    COALESCE(JSON_VALUE([item].[value], '$.Kind'), JSON_VALUE([item].[value], '$.kind')),
                    COALESCE(JSON_VALUE([item].[value], '$.Reference'), JSON_VALUE([item].[value], '$.reference')),
                    COALESCE(JSON_VALUE([item].[value], '$.Label'), JSON_VALUE([item].[value], '$.label'))
                FROM [knowledge].[knowledge_entry] AS [entry]
                CROSS APPLY OPENJSON([entry].[written_against_json]) AS [item]
                WHERE ISJSON([entry].[written_against_json]) = 1;
                """);

            foreach (var table in new[] { "knowledge_entry_written_against", "knowledge_entry_review_reason",
                         "knowledge_screen_change" })
                migrationBuilder.AddTenantPolicy("knowledge", table);

            migrationBuilder.CreateIndex(
                name: "ix_question_notification_tenant_id_entry_id",
                schema: "knowledge",
                table: "question_notification",
                columns: new[] { "tenant_id", "entry_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_question_notification_target",
                schema: "knowledge",
                table: "question_notification",
                sql: "(question_id IS NOT NULL AND entry_id IS NULL) OR (question_id IS NULL AND entry_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_tenant_id_needs_review_needs_review_since_entry_id",
                schema: "knowledge",
                table: "knowledge_entry",
                columns: new[] { "tenant_id", "needs_review", "needs_review_since", "entry_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_entry_needs_review_state",
                schema: "knowledge",
                table: "knowledge_entry",
                sql: "(needs_review = 1 AND needs_review_since IS NOT NULL AND published_version IS NOT NULL) OR (needs_review = 0 AND needs_review_since IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_entry_published_version",
                schema: "knowledge",
                table: "knowledge_entry",
                sql: "published_version IS NULL OR (published_version >= 1 AND published_version <= current_version)");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_review_reason_tenant_id_entry_id_cleared_at_flagged_at_reason_id",
                schema: "knowledge",
                table: "knowledge_entry_review_reason",
                columns: new[] { "tenant_id", "entry_id", "cleared_at", "flagged_at", "reason_id" });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_entry_review_reason_tenant_id_entry_id_kind_reference_trigger_id",
                schema: "knowledge",
                table: "knowledge_entry_review_reason",
                columns: new[] { "tenant_id", "entry_id", "kind", "reference", "trigger_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_entry_written_against_tenant_id_kind_reference_entry_id_version",
                schema: "knowledge",
                table: "knowledge_entry_written_against",
                columns: new[] { "tenant_id", "kind", "reference", "entry_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_screen_change_tenant_id_screen_reference_changed_at_change_id",
                schema: "knowledge",
                table: "knowledge_screen_change",
                columns: new[] { "tenant_id", "screen_reference", "changed_at", "change_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_question_notification_tenant_id_entry_id",
                schema: "knowledge",
                table: "question_notification",
                columns: new[] { "tenant_id", "entry_id" },
                principalSchema: "knowledge",
                principalTable: "knowledge_entry",
                principalColumns: new[] { "tenant_id", "entry_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_question_notification_tenant_id_entry_id",
                schema: "knowledge",
                table: "question_notification");

            foreach (var table in new[] { "knowledge_entry_review_reason", "knowledge_entry_written_against",
                         "knowledge_screen_change" })
                migrationBuilder.RemoveTenantPolicy("knowledge", table);

            migrationBuilder.DropTable(
                name: "knowledge_entry_review_reason",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_entry_written_against",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "knowledge_screen_change",
                schema: "knowledge");

            migrationBuilder.DropIndex(
                name: "ix_question_notification_tenant_id_entry_id",
                schema: "knowledge",
                table: "question_notification");

            migrationBuilder.DropCheckConstraint(
                name: "ck_question_notification_target",
                schema: "knowledge",
                table: "question_notification");

            migrationBuilder.DropIndex(
                name: "ix_knowledge_entry_tenant_id_needs_review_needs_review_since_entry_id",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_entry_needs_review_state",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_entry_published_version",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.DropColumn(
                name: "entry_id",
                schema: "knowledge",
                table: "question_notification");

            migrationBuilder.DropColumn(
                name: "needs_review",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.DropColumn(
                name: "needs_review_since",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.DropColumn(
                name: "published_version",
                schema: "knowledge",
                table: "knowledge_entry");

            migrationBuilder.AlterColumn<Guid>(
                name: "question_id",
                schema: "knowledge",
                table: "question_notification",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
