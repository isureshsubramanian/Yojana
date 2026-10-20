using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <summary>
    /// Benchmark finding 2026-10-20 (500-serial shipment): every merged-from index held one entry per row although only
    /// rows moved by a unit merge carry the column (11.5 GB on a 152 M-event ledger, maintained by every append), and the
    /// sale-time owner lookup paid a clustered key lookup per unit. The merged-from indexes become filtered to merged rows
    /// and the live-entitlement index covers the owner.
    /// Each index is rebuilt in place with DROP_EXISTING, so the build can read the existing index rather than the table.
    /// The three ledger tables keep identical, partition-aligned indexes, which SWITCH PARTITION requires (LedgerDdl).
    /// </summary>
    public partial class P9PerfMergeAndCoverIndexes : Migration
    {
        private const string Schema = SerialisationDbContext.SchemaName;
        private const string Scheme = SerialisationDbContext.PartitionScheme;

        private static readonly (string Table, string Index)[] MergedFrom =
        [
            ("assembly_link", "ix_assembly_link_tenant_id_merged_from_unit_id"),
            ("build_record", "ix_build_record_tenant_id_merged_from_unit_id"),
            ("custody_episode", "ix_custody_episode_tenant_id_merged_from_unit_id"),
            ("entitlement", "ix_entitlement_tenant_id_merged_from_unit_id"),
            ("entitlement_requirement", "ix_entitlement_requirement_tenant_id_merged_from_unit_id"),
            ("search_entry", "ix_search_entry_tenant_id_merged_from_unit_id"),
            ("search_term", "ix_search_term_tenant_id_merged_from_unit_id"),
            ("selection_set_member", "ix_selection_set_member_tenant_id_merged_from_unit_id"),
            ("serial_range_member", "ix_serial_range_member_tenant_id_merged_from_unit_id"),
            ("unit_identifier", "ix_unit_identifier_tenant_id_merged_from_unit_id"),
            ("unit_item_assignment", "ix_unit_item_assignment_tenant_id_merged_from_unit_id"),
        ];

        private static readonly (string Table, string Index)[] LedgerMergedFrom =
        [
            ("unit_event", "ix_unit_event_tenant_id_merged_from_unit_id_event_time"),
            ("unit_event_archive", "ix_unit_event_archive_tenant_id_merged_from_unit_id_event_time"),
            ("unit_event_archive_stage", "ix_unit_event_archive_stage_tenant_id_merged_from_unit_id_event_time"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, filtered: true);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, filtered: false);

        private static void Rebuild(MigrationBuilder migrationBuilder, bool filtered)
        {
            var where = filtered ? $" WHERE {SerialisationDbContext.MergedFromFilter}" : "";
            foreach (var (table, index) in MergedFrom)
            {
                migrationBuilder.Sql($"""
                    CREATE NONCLUSTERED INDEX [{index}] ON [{Schema}].[{table}] ([tenant_id], [merged_from_unit_id]){where}
                        WITH (DROP_EXISTING = ON);
                    """);
            }

            foreach (var (table, index) in LedgerMergedFrom)
            {
                migrationBuilder.Sql($"""
                    CREATE NONCLUSTERED INDEX [{index}] ON [{Schema}].[{table}] ([tenant_id], [merged_from_unit_id], [event_time]){where}
                        WITH (DROP_EXISTING = ON) ON [{Scheme}] ([event_time]);
                    """);
            }

            var include = filtered ? " INCLUDE ([owner_party_ref])" : "";
            migrationBuilder.Sql($"""
                CREATE UNIQUE NONCLUSTERED INDEX [ux_entitlement_tenant_id_unit_id_coverage_family]
                    ON [{Schema}].[entitlement] ([tenant_id], [unit_id], [coverage_family]){include}
                    WHERE [status] <> 'closed'
                    WITH (DROP_EXISTING = ON);
                """);
        }
    }
}
