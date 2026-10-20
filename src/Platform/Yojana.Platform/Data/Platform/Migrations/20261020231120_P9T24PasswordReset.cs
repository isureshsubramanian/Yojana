using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T24PasswordReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "platform",
                table: "app_user",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "temporary_password_expires_at",
                schema: "platform",
                table: "app_user",
                type: "datetime2(3)",
                nullable: true);

            // Global by design: a reset begins before sign-in, when there is no tenant context, and one identity may
            // span tenants. No token is stored, only its SHA-256 digest, so this table does not receive tenant RLS.
            migrationBuilder.CreateTable(
                name: "password_reset_token",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    superseded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_token", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_reset_token_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_user_id_created_at",
                schema: "platform",
                table: "password_reset_token",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_password_reset_token_token_hash",
                schema: "platform",
                table: "password_reset_token",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "password_reset_token",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "platform",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "temporary_password_expires_at",
                schema: "platform",
                table: "app_user");
        }
    }
}
