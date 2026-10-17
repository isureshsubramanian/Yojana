using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T09MergeUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_unit_identifier_tenant_id_scheme_scope_key_normalised_value",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_item_assignment",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_identifier",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event_archive_stage",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event_archive",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "serial_range_member",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "selection_set_member",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "search_term",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "search_entry",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement_requirement",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "custody_episode",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "build_record",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "assembly_link",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "unit_merge_record",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    survivor_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    absorbed_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    merged_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    reversal_data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    reversed_by_correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    reversed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_merge_record", x => new { x.tenant_id, x.correction_id });
                    table.CheckConstraint("ck_unit_merge_record_reversal_data_json", "ISJSON(reversal_data) = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_unit_item_assignment_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "unit_item_assignment",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_identifier_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_unit_identifier_tenant_id_scheme_scope_key_normalised_value",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "scheme", "scope_key", "normalised_value" },
                unique: true,
                filter: "[valid_to] IS NULL AND ([type] <> 'vendor_serial' OR [duplicate_acknowledged] = 0)");

            migrationBuilder.CreateIndex(
                name: "ix_unit_event_archive_stage_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event_archive_stage",
                columns: new[] { "tenant_id", "merged_from_unit_id", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_event_archive_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event_archive",
                columns: new[] { "tenant_id", "merged_from_unit_id", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_event_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event",
                columns: new[] { "tenant_id", "merged_from_unit_id", "event_time" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_member_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "serial_range_member",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_selection_set_member_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "selection_set_member",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_term_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "search_term",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_entry_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "search_entry",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_requirement_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement_requirement",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_build_record_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "build_record",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assembly_link_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "assembly_link",
                columns: new[] { "tenant_id", "merged_from_unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_merge_record_tenant_id_reversed_by_correction_id",
                schema: "serialisation",
                table: "unit_merge_record",
                columns: new[] { "tenant_id", "reversed_by_correction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_merge_record_tenant_id_survivor_unit_id_absorbed_unit_id_reversed_at",
                schema: "serialisation",
                table: "unit_merge_record",
                columns: new[] { "tenant_id", "survivor_unit_id", "absorbed_unit_id", "reversed_at" });

            migrationBuilder.AddTenantPolicy("serialisation", "unit_merge_record");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "unit_merge_record");

            migrationBuilder.DropTable(
                name: "unit_merge_record",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_unit_item_assignment_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "unit_item_assignment");

            migrationBuilder.DropIndex(
                name: "ix_unit_identifier_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.DropIndex(
                name: "ux_unit_identifier_tenant_id_scheme_scope_key_normalised_value",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.DropIndex(
                name: "ix_unit_event_archive_stage_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event_archive_stage");

            migrationBuilder.DropIndex(
                name: "ix_unit_event_archive_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event_archive");

            migrationBuilder.DropIndex(
                name: "ix_unit_event_tenant_id_merged_from_unit_id_event_time",
                schema: "serialisation",
                table: "unit_event");

            migrationBuilder.DropIndex(
                name: "ix_serial_range_member_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "serial_range_member");

            migrationBuilder.DropIndex(
                name: "ix_selection_set_member_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "selection_set_member");

            migrationBuilder.DropIndex(
                name: "ix_search_term_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "search_term");

            migrationBuilder.DropIndex(
                name: "ix_search_entry_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "search_entry");

            migrationBuilder.DropIndex(
                name: "ix_entitlement_requirement_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement_requirement");

            migrationBuilder.DropIndex(
                name: "ix_entitlement_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropIndex(
                name: "ix_custody_episode_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "custody_episode");

            migrationBuilder.DropIndex(
                name: "ix_build_record_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "build_record");

            migrationBuilder.DropIndex(
                name: "ix_assembly_link_tenant_id_merged_from_unit_id",
                schema: "serialisation",
                table: "assembly_link");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_item_assignment");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_identifier");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event_archive_stage");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event_archive");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "unit_event");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "serial_range_member");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "selection_set_member");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "search_term");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "search_entry");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement_requirement");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "entitlement");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "custody_episode");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "build_record");

            migrationBuilder.DropColumn(
                name: "merged_from_unit_id",
                schema: "serialisation",
                table: "assembly_link");

            migrationBuilder.CreateIndex(
                name: "ux_unit_identifier_tenant_id_scheme_scope_key_normalised_value",
                schema: "serialisation",
                table: "unit_identifier",
                columns: new[] { "tenant_id", "scheme", "scope_key", "normalised_value" },
                unique: true,
                filter: "[valid_to] IS NULL");
        }
    }
}
