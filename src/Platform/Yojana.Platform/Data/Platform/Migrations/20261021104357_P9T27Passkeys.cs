using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T27Passkeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "passkey_ceremony",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    options_json = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_passkey_ceremony", x => x.id);
                    table.ForeignKey(
                        name: "fk_passkey_ceremony_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_passkey",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    credential_id = table.Column<byte[]>(type: "varbinary(1023)", maxLength: 1023, nullable: false),
                    public_key = table.Column<byte[]>(type: "varbinary(2048)", maxLength: 2048, nullable: false),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    aaguid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    transports = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    backup_eligible = table.Column<bool>(type: "bit", nullable: false),
                    backed_up = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_passkey", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_passkey_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_passkey_ceremony_expires_at",
                schema: "platform",
                table: "passkey_ceremony",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_passkey_ceremony_user_id",
                schema: "platform",
                table: "passkey_ceremony",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_passkey_user_id_created_at",
                schema: "platform",
                table: "user_passkey",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_user_passkey_credential_id",
                schema: "platform",
                table: "user_passkey",
                column: "credential_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "passkey_ceremony",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_passkey",
                schema: "platform");
        }
    }
}
