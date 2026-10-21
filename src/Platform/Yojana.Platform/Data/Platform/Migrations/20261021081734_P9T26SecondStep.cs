using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T26SecondStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both tables are global by design: no tenant exists before sign-in and one user identity can span several
            // tenants. They therefore have no tenant_id and no tenant RLS policy; every row is owned by app_user.
            migrationBuilder.CreateTable(
                name: "user_authenticator",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sealed_secret = table.Column<byte[]>(type: "varbinary(4096)", maxLength: 4096, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    last_used_step = table.Column<long>(type: "bigint", nullable: true),
                    replaced_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_authenticator", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_authenticator_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_recovery_code",
                schema: "platform",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    batch_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_recovery_code", x => new { x.user_id, x.code_hash });
                    table.ForeignKey(
                        name: "fk_user_recovery_code_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_authenticator_user_id_status_created_at",
                schema: "platform",
                table: "user_authenticator",
                columns: new[] { "user_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_code_user_id_batch_id_used_at",
                schema: "platform",
                table: "user_recovery_code",
                columns: new[] { "user_id", "batch_id", "used_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_authenticator",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_recovery_code",
                schema: "platform");
        }
    }
}
