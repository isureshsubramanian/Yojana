using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T36bNormalisedKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "family_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // TIGHT COUPLING (stored serial match keys): this backfill and SourceExtractor call
            // migration.canonical_serial with all strip flags false. Changing that function without rebuilding this
            // column would make contract rows extracted before and after this migration compare differently.
            migrationBuilder.Sql("""
                SET NOCOUNT ON;
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (10000) [migration].[stg_src_ctr1]
                       SET [normalised_internal_serial] =
                           [migration].[canonical_serial]([internal_serial], 0, 0, 0)
                     WHERE [normalised_internal_serial] IS NULL AND [internal_serial] IS NOT NULL;
                    IF @@ROWCOUNT = 0 BREAK;
                END;
                """, suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code", "family_normalised_serial_number" })
                .Annotation("SqlServer:Include", new[] { "source_pk", "system_number", "serial_number" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code", "family_normalised_internal_serial" })
                .Annotation("SqlServer:Include", new[] { "source_pk", "card_id", "internal_serial", "warranty_start", "warranty_end" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code", "family_normalised_internal_serial" })
                .Annotation("SqlServer:Include", new[] { "source_pk", "contract_id", "internal_serial", "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code", "normalised_internal_serial" })
                .Annotation("SqlServer:Include", new[] { "source_pk", "contract_id", "internal_serial", "start_date", "end_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropColumn(
                name: "family_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropColumn(
                name: "family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropColumn(
                name: "family_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropColumn(
                name: "normalised_internal_serial",
                schema: "migration",
                table: "stg_src_ctr1");
        }
    }
}
