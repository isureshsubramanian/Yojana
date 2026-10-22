using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T35StagingMatchKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oscl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osbq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_orrr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_octt_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_octr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oatc_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ins1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ains_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains");

            migrationBuilder.AddColumn<string>(
                name: "normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_osrn",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_oins",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // TIGHT COUPLING (stored serial match keys): this backfill and SourceExtractor's upsert both call
            // migration.canonical_serial with all strip flags false. That function owns trim-and-uppercase
            // semantics; changing it without rebuilding these four columns leaves old and new staging rows unequal.
            // Bounded updates keep the transaction log and lock footprint suitable for SQL Server Express.
            migrationBuilder.Sql("""
                SET NOCOUNT ON;
                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (10000) [migration].[stg_src_osrn]
                       SET [normalised_serial_number] = [migration].[canonical_serial]([serial_number], 0, 0, 0),
                           [normalised_manufacturer_serial] = [migration].[canonical_serial]([manufacturer_serial], 0, 0, 0)
                     WHERE ([normalised_serial_number] IS NULL AND [serial_number] IS NOT NULL)
                        OR ([normalised_manufacturer_serial] IS NULL AND [manufacturer_serial] IS NOT NULL);
                    IF @@ROWCOUNT = 0 BREAK;
                END;

                WHILE 1 = 1
                BEGIN
                    UPDATE TOP (10000) [migration].[stg_src_oins]
                       SET [normalised_internal_serial] = [migration].[canonical_serial]([internal_serial], 0, 0, 0),
                           [normalised_manufacturer_serial] = [migration].[canonical_serial]([manufacturer_serial], 0, 0, 0)
                     WHERE ([normalised_internal_serial] IS NULL AND [internal_serial] IS NOT NULL)
                        OR ([normalised_manufacturer_serial] IS NULL AND [manufacturer_serial] IS NOT NULL);
                    IF @@ROWCOUNT = 0 BREAK;
                END;
                """, suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_system_number",
                schema: "migration",
                table: "stg_src_osrq",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code", "system_number" })
                .Annotation("SqlServer:Include", new[] { "source_pk", "warehouse_code", "quantity" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_source_system_item_code_normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "source_system", "item_code", "normalised_manufacturer_serial" })
                .Annotation("SqlServer:Include", new[] { "batch_id", "absent_since_batch_id", "source_pk", "system_number", "serial_number", "manufacturer_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_source_system_item_code_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "source_system", "item_code", "normalised_serial_number" })
                .Annotation("SqlServer:Include", new[] { "batch_id", "absent_since_batch_id", "source_pk", "system_number", "serial_number", "manufacturer_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oscl_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osbq_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_orrr_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_system_absent_since_batch_id_item_code",
                schema: "migration",
                table: "stg_src_oitm",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "item_code" })
                .Annotation("SqlServer:Include", new[] { "management_method" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_card_id",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "batch_id", "source_system", "card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_source_system_item_code_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "source_system", "item_code", "normalised_internal_serial" })
                .Annotation("SqlServer:Include", new[] { "batch_id", "absent_since_batch_id", "source_pk", "card_id", "customer_code", "internal_serial", "manufacturer_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_source_system_item_code_normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "source_system", "item_code", "normalised_manufacturer_serial" })
                .Annotation("SqlServer:Include", new[] { "batch_id", "absent_since_batch_id", "source_pk", "card_id", "customer_code", "internal_serial", "manufacturer_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octt_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octr_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oatc_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ins1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ains_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains",
                columns: new[] { "tenant_id", "batch_id", "source_system", "absent_since_batch_id", "source_pk" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_system_absent_since_batch_id_item_code_system_number",
                schema: "migration",
                table: "stg_src_osrq");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrn_tenant_id_source_system_item_code_normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osrn_tenant_id_source_system_item_code_normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oscl_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_osbq_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_orrr_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_system_absent_since_batch_id_item_code",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_system_card_id",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_source_system_item_code_normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oins_tenant_id_source_system_item_code_normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_octt_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_octr_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_oatc_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ins1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1");

            migrationBuilder.DropIndex(
                name: "ix_stg_src_ains_tenant_id_batch_id_source_system_absent_since_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains");

            migrationBuilder.DropColumn(
                name: "normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropColumn(
                name: "normalised_serial_number",
                schema: "migration",
                table: "stg_src_osrn");

            migrationBuilder.DropColumn(
                name: "normalised_internal_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.DropColumn(
                name: "normalised_manufacturer_serial",
                schema: "migration",
                table: "stg_src_oins");

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrq",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osrn_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osrn",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oscl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oscl",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_osbq_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_osbq",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_orrr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_orrr",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitm_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitm",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oitl_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oitl",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oins_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oins",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octt_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octt",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_octr_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_octr",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_oatc_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_oatc",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_marketing_document_headers_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_marketing_document_headers",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_itl1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_itl1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ins1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ins1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ctr1_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ctr1",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });

            migrationBuilder.CreateIndex(
                name: "ix_stg_src_ains_tenant_id_batch_id_source_pk",
                schema: "migration",
                table: "stg_src_ains",
                columns: new[] { "tenant_id", "batch_id", "source_pk" });
        }
    }
}
