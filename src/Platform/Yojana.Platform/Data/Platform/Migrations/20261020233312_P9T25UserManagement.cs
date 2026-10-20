using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class P9T25UserManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deactivated_at",
                schema: "platform",
                table: "user_tenant_membership",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "version",
                schema: "platform",
                table: "user_tenant_membership",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.CreateTable(
                name: "user_invitation",
                schema: "platform",
                // Deliberately global: acceptance runs before tenant resolution. TenantId is still the leading physical
                // column and every tenant-side query filters it explicitly; adding tenant RLS would make anonymous
                // inspection and acceptance unable to see any row.
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    roles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    token_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    accepted_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_invitation", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_invitation_accepted_user_id",
                        column: x => x.accepted_user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_user_invitation_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_user_invitation_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_user_invitation_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_membership_deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership",
                column: "deactivated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_accepted_user_id",
                schema: "platform",
                table: "user_invitation",
                column: "accepted_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_created_by_user_id",
                schema: "platform",
                table: "user_invitation",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_expires_at_id",
                schema: "platform",
                table: "user_invitation",
                columns: new[] { "expires_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_revoked_by_user_id",
                schema: "platform",
                table: "user_invitation",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_tenant_id_display_name_id",
                schema: "platform",
                table: "user_invitation",
                columns: new[] { "tenant_id", "display_name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_invitation_tenant_id_email_expires_at",
                schema: "platform",
                table: "user_invitation",
                columns: new[] { "tenant_id", "email", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_user_invitation_token_hash",
                schema: "platform",
                table: "user_invitation",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_user_tenant_membership_deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership",
                column: "deactivated_by_user_id",
                principalSchema: "platform",
                principalTable: "app_user",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_tenant_membership_deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.DropTable(
                name: "user_invitation",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "ix_user_tenant_membership_deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.DropColumn(
                name: "deactivated_at",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.DropColumn(
                name: "deactivated_by_user_id",
                schema: "platform",
                table: "user_tenant_membership");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "platform",
                table: "user_tenant_membership");
        }
    }
}
