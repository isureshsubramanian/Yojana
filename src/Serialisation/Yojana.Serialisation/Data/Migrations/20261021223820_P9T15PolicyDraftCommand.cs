using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P9T15PolicyDraftCommand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "command_id",
                schema: "serialisation",
                table: "warranty_policy_version",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_warranty_policy_version_tenant_id_command_id",
                schema: "serialisation",
                table: "warranty_policy_version",
                columns: new[] { "tenant_id", "command_id" },
                unique: true,
                filter: "[command_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_warranty_policy_version_tenant_id_command_id",
                schema: "serialisation",
                table: "warranty_policy_version");

            migrationBuilder.DropColumn(
                name: "command_id",
                schema: "serialisation",
                table: "warranty_policy_version");
        }
    }
}
