using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Knowledge.Migrations
{
    /// <inheritdoc />
    public partial class P13T04Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These two non-tenant-led indexes are the smallest possible exception to the index convention: SQL Server
            // full-text requires a unique, non-null, single-column key. Tenant isolation still comes from the composite
            // primary keys and RLS. They are deliberately migration-owned so ordinary EF queries cannot accidentally
            // start depending on a cross-tenant access path.
            migrationBuilder.Sql("CREATE UNIQUE INDEX [ux_question_reply_reply_id] ON [knowledge].[question_reply] ([reply_id])");
            migrationBuilder.Sql("CREATE UNIQUE INDEX [ux_question_question_id] ON [knowledge].[question] ([question_id])");

            // Coupled to the single-column unique indexes above and the full-text query in
            // SqlServerFullTextQuestionSearch. SQL Server cannot use the tenant-first composite keys as full-text
            // keys; changing these names requires rebuilding both full-text indexes or search stops working.
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'knowledge_questions')
                        EXEC(N'CREATE FULLTEXT CATALOG [knowledge_questions] WITH ACCENT_SENSITIVITY = OFF');
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[question]'))
                        EXEC(N'CREATE FULLTEXT INDEX ON [knowledge].[question]
                            ([title] LANGUAGE 1033, [body] LANGUAGE 1033)
                            KEY INDEX [ux_question_question_id] ON [knowledge_questions] WITH CHANGE_TRACKING AUTO');
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[question_reply]'))
                        EXEC(N'CREATE FULLTEXT INDEX ON [knowledge].[question_reply]
                            ([body] LANGUAGE 1033)
                            KEY INDEX [ux_question_reply_reply_id] ON [knowledge_questions] WITH CHANGE_TRACKING AUTO');
                END
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[question_reply]'))
                        EXEC(N'DROP FULLTEXT INDEX ON [knowledge].[question_reply]');
                    IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[knowledge].[question]'))
                        EXEC(N'DROP FULLTEXT INDEX ON [knowledge].[question]');
                    IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'knowledge_questions')
                        EXEC(N'DROP FULLTEXT CATALOG [knowledge_questions]');
                END
                """, suppressTransaction: true);

            migrationBuilder.DropIndex(
                name: "ux_question_reply_reply_id",
                schema: "knowledge",
                table: "question_reply");

            migrationBuilder.DropIndex(
                name: "ux_question_question_id",
                schema: "knowledge",
                table: "question");
        }
    }
}
