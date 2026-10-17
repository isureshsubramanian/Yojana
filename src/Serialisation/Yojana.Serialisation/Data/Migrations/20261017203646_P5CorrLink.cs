using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5CorrLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_replacement_link_tenant_id_warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.AddColumn<Guid>(
                name: "corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "correction_command_id",
                schema: "serialisation",
                table: "replacement_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "corrected_by_link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "warranty_replacement_id" });

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_correction_command_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "correction_command_id" },
                unique: true,
                filter: "[correction_command_id] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_replacement_link_tenant_id_corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "corrected_by_link_id" },
                principalSchema: "serialisation",
                principalTable: "replacement_link",
                principalColumns: new[] { "tenant_id", "replacement_link_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_replacement_link_tenant_id_corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ix_replacement_link_tenant_id_corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ix_replacement_link_tenant_id_warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ux_replacement_link_tenant_id_correction_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "corrected_by_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropColumn(
                name: "correction_command_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "warranty_replacement_id" },
                unique: true);
        }
    }
}
