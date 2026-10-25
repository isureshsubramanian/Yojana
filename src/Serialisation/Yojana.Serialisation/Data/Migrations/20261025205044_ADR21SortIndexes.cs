using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class ADR21SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from",
                schema: "serialisation",
                table: "custody_episode");

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_item_id_unit_id",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "item_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_last_event_time_unit_id",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "last_event_time", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_current_tenant_id_lifecycle_status_unit_id",
                schema: "serialisation",
                table: "unit_current",
                columns: new[] { "tenant_id", "lifecycle_status", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_tenant_id_scheme_reserved_at_range_id",
                schema: "serialisation",
                table: "serial_range",
                columns: new[] { "tenant_id", "scheme", "reserved_at", "range_id" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_tenant_id_scheme_start_ordinal_range_id",
                schema: "serialisation",
                table: "serial_range",
                columns: new[] { "tenant_id", "scheme", "start_ordinal", "range_id" });

            migrationBuilder.CreateIndex(
                name: "ix_serial_range_tenant_id_scheme_status_range_id",
                schema: "serialisation",
                table: "serial_range",
                columns: new[] { "tenant_id", "scheme", "status", "range_id" });

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from_unit_id",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "kind", "party_type", "valid_from", "unit_id" })
                .Annotation("SqlServer:Include", new[] { "episode_id", "party_ref", "source", "opening_document_type" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_issued_at_tracking_number",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "issued_at", "tracking_number" });

            migrationBuilder.CreateIndex(
                name: "ix_certificate_instance_tenant_id_status_tracking_number",
                schema: "serialisation",
                table: "certificate_instance",
                columns: new[] { "tenant_id", "status", "tracking_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_unit_current_tenant_id_item_id_unit_id",
                schema: "serialisation",
                table: "unit_current");

            migrationBuilder.DropIndex(
                name: "ix_unit_current_tenant_id_last_event_time_unit_id",
                schema: "serialisation",
                table: "unit_current");

            migrationBuilder.DropIndex(
                name: "ix_unit_current_tenant_id_lifecycle_status_unit_id",
                schema: "serialisation",
                table: "unit_current");

            migrationBuilder.DropIndex(
                name: "ix_serial_range_tenant_id_scheme_reserved_at_range_id",
                schema: "serialisation",
                table: "serial_range");

            migrationBuilder.DropIndex(
                name: "ix_serial_range_tenant_id_scheme_start_ordinal_range_id",
                schema: "serialisation",
                table: "serial_range");

            migrationBuilder.DropIndex(
                name: "ix_serial_range_tenant_id_scheme_status_range_id",
                schema: "serialisation",
                table: "serial_range");

            migrationBuilder.DropIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from_unit_id",
                schema: "serialisation",
                table: "custody_episode");

            migrationBuilder.DropIndex(
                name: "ix_certificate_instance_tenant_id_issued_at_tracking_number",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.DropIndex(
                name: "ix_certificate_instance_tenant_id_status_tracking_number",
                schema: "serialisation",
                table: "certificate_instance");

            migrationBuilder.CreateIndex(
                name: "ix_custody_episode_tenant_id_kind_party_type_valid_from",
                schema: "serialisation",
                table: "custody_episode",
                columns: new[] { "tenant_id", "kind", "party_type", "valid_from" })
                .Annotation("SqlServer:Include", new[] { "unit_id", "episode_id", "party_ref", "source", "opening_document_type" });
        }
    }
}
