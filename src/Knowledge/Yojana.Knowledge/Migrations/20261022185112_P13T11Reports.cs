using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T11Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "accepted_at",
                schema: "knowledge",
                table: "question",
                type: "datetime2(3)",
                nullable: true);

            // Older answered questions did not store the acceptance instant. Updated-at is the closest durable fact.
            // Bounded, set-based batches keep the transaction log suitable for SQL Server Express.
            migrationBuilder.Sql("""
                SET NOCOUNT ON;
                ALTER SECURITY POLICY [sec].[tenant_policy_knowledge] WITH (STATE = OFF);
                BEGIN TRY
                    WHILE 1 = 1
                    BEGIN
                        UPDATE TOP (10000) [knowledge].[question]
                           SET [accepted_at] = [updated_at]
                         WHERE [status] = N'answered' AND [accepted_at] IS NULL;
                        IF @@ROWCOUNT = 0 BREAK;
                    END;
                    ALTER SECURITY POLICY [sec].[tenant_policy_knowledge] WITH (STATE = ON);
                END TRY
                BEGIN CATCH
                    ALTER SECURITY POLICY [sec].[tenant_policy_knowledge] WITH (STATE = ON);
                    THROW;
                END CATCH;
                """, suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "question_similar_entry",
                schema: "knowledge",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    question_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rank = table.Column<int>(type: "int", nullable: false),
                    matched_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_question_similar_entry", x => new { x.tenant_id, x.question_id, x.entry_id });
                    table.CheckConstraint("ck_question_similar_entry_rank", "rank BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "fk_question_similar_entry_tenant_id_entry_id",
                        columns: x => new { x.tenant_id, x.entry_id },
                        principalSchema: "knowledge",
                        principalTable: "knowledge_entry",
                        principalColumns: new[] { "tenant_id", "entry_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_question_similar_entry_tenant_id_question_id",
                        columns: x => new { x.tenant_id, x.question_id },
                        principalSchema: "knowledge",
                        principalTable: "question",
                        principalColumns: new[] { "tenant_id", "question_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddTenantPolicy("knowledge", "question_similar_entry");

            migrationBuilder.CreateIndex(
                name: "ix_training_progress_tenant_id_read_at_entry_id_user_id",
                schema: "knowledge",
                table: "training_progress",
                columns: new[] { "tenant_id", "read_at", "entry_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_question_tenant_id_asked_at_question_id",
                schema: "knowledge",
                table: "question",
                columns: new[] { "tenant_id", "asked_at", "question_id" })
                .Annotation("SqlServer:Include", new[] { "status", "record_kind", "record_id", "directed_to_role", "accepted_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_question_answered_state",
                schema: "knowledge",
                table: "question",
                sql: "(status = 'answered' AND accepted_reply_id IS NOT NULL AND accepted_at IS NOT NULL) OR status <> 'answered'");

            migrationBuilder.CreateIndex(
                name: "ix_question_similar_entry_tenant_id_entry_id_matched_at_question_id",
                schema: "knowledge",
                table: "question_similar_entry",
                columns: new[] { "tenant_id", "entry_id", "matched_at", "question_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("knowledge", "question_similar_entry");

            migrationBuilder.DropTable(
                name: "question_similar_entry",
                schema: "knowledge");

            migrationBuilder.DropIndex(
                name: "ix_training_progress_tenant_id_read_at_entry_id_user_id",
                schema: "knowledge",
                table: "training_progress");

            migrationBuilder.DropIndex(
                name: "ix_question_tenant_id_asked_at_question_id",
                schema: "knowledge",
                table: "question");

            migrationBuilder.DropCheckConstraint(
                name: "ck_question_answered_state",
                schema: "knowledge",
                table: "question");

            migrationBuilder.DropColumn(
                name: "accepted_at",
                schema: "knowledge",
                table: "question");

        }
    }
}
