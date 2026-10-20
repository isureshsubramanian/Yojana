using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T22SignInSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These two stores are intentionally global like app_user and user_tenant_membership. Authentication
            // writes them before a tenant exists, and security-event reads span a tenant's current memberships;
            // adding the tenant RLS predicate would block pre-selection writes and hide the membership side of R5.
            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                schema: "platform",
                table: "app_user",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "security_event",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    client_address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    reason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    unknown_user_hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_event_actor_user_id",
                        column: x => x.actor_user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_security_event_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_security_event_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sign_in_throttle",
                schema: "platform",
                columns: table => new
                {
                    key_kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    key_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    window_started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    failure_count = table.Column<int>(type: "int", nullable: false),
                    lockout_count = table.Column<int>(type: "int", nullable: false),
                    locked_until = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sign_in_throttle", x => new { x.key_kind, x.key_hash });
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_event_actor_user_id",
                schema: "platform",
                table: "security_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_event_occurred_at_id",
                schema: "platform",
                table: "security_event",
                columns: new[] { "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_event_tenant_id_occurred_at_id",
                schema: "platform",
                table: "security_event",
                columns: new[] { "tenant_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_event_user_id_occurred_at_id",
                schema: "platform",
                table: "security_event",
                columns: new[] { "user_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_sign_in_throttle_expires_at",
                schema: "platform",
                table: "sign_in_throttle",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_event",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "sign_in_throttle",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "platform",
                table: "app_user");
        }
    }
}
