using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P13T10TrainingPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_tenant_membership_tenant_id",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                schema: "platform",
                table: "user_tenant_membership",
                type: "datetime2(3)",
                nullable: true);

            // The old membership table did not record its own creation time. The account creation time is the only
            // durable historical lower bound available, so existing memberships use it; new invitations stamp the
            // actual membership time. Batches keep the upgrade suitable for SQL Server Express installations.
            migrationBuilder.Sql("""
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (10000) [membership]
                    SET [created_at] = [user].[created_at]
                    FROM [platform].[user_tenant_membership] AS [membership]
                    INNER JOIN [platform].[app_user] AS [user] ON [user].[id] = [membership].[user_id]
                    WHERE [membership].[created_at] IS NULL;
                    IF @@ROWCOUNT = 0 BREAK;
                END;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "created_at",
                schema: "platform",
                table: "user_tenant_membership",
                type: "datetime2(3)",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()",
                oldClrType: typeof(DateTime),
                oldType: "datetime2(3)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_membership_tenant_id_created_at_user_id",
                schema: "platform",
                table: "user_tenant_membership",
                columns: new[] { "tenant_id", "created_at", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_tenant_membership_tenant_id_created_at_user_id",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_membership_tenant_id",
                schema: "platform",
                table: "user_tenant_membership",
                column: "tenant_id");
        }
    }
}
