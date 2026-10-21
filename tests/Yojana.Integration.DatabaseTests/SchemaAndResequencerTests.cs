using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Ordering;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class SchemaAndResequencerTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Integration_schema_contains_the_task_tables()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT TABLE_NAME AS Value FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'integration'").ToListAsync();
        AssertSuperset(tables, "connection", "inbound_message", "idempotency_index", "canonical_document",
            "reference_projection", "connection_capability", "connection_degradation_setting", "connection_gateway_setting",
            "outbound_command", "exception_task", "resequence_item", "external_id_map", "connector_catalogue",
            "tenant_data_key", "connection_credential", "tenant_data_key_rewrap_checkpoint", "connection_event",
            "connection_throttle_profile", "connection_notification_source", "connection_poll_watermark",
            "dead_letter_item", "dead_letter_event", "manual_outbound_task", "connection_onboarding_confirmation");
        AssertSuperset(tables, "relay", "relay_request", "relay_nonce");
        AssertSuperset(tables, "coexistence_baseline", "coexistence_conflict", "coexistence_scan_state",
            "parallel_run_plan", "parallel_run_confirmation");
        AssertSuperset(tables, "connection_health", "cx01_survey", "connection_notification_receipt",
            "connection_notification_watchdog", "outbound_document_receipt");
        AssertSuperset(tables, "reconciliation_run", "reconciliation_entity_day",
            "reconciliation_location", "backfill_cursor");
        var migrationTables = await db.Database.SqlQueryRaw<string>("SELECT TABLE_NAME AS Value FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'migration'").ToListAsync();
        AssertSuperset(migrationTables, "migration_source", "migration_batch", "migration_batch_table",
            "migration_profile_run", "migration_profile_result",
            "stg_src_osrn", "stg_src_osrq", "stg_src_osbq", "stg_src_oitl", "stg_src_itl1",
            "stg_src_oins", "stg_src_ains", "stg_src_ins1", "stg_src_octr", "stg_src_ctr1",
            "stg_src_octt", "stg_src_oscl", "stg_src_orrr", "stg_src_marketing_document_headers",
            "stg_src_oatc", "stg_src_oitm", "template_mapping", "template_mapping_block",
            "migration_canon_run", "migration_unit_candidate", "migration_unit_candidate_source",
            "migration_event_candidate", "migration_entitlement_candidate", "migration_review_item",
            "migration_review_decision", "migration_reconstruction_run", "migration_history_event",
            "migration_ownership_episode", "migration_reconstruction_log", "migration_map_run",
            "migration_plan_unit", "migration_plan_identifier", "migration_plan_event",
            "migration_plan_ownership", "migration_plan_replacement_link", "migration_plan_entitlement",
            "migration_plan_warranty_evidence", "migration_plan_attachment", "migration_plan_certificate",
            "migration_plan_gap", "migration_plan_log");
        var columns = await db.Database.SqlQueryRaw<string>("SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'integration' AND TABLE_NAME = 'manual_outbound_task'").ToListAsync();
        Assert.Contains("desk", columns);
        var healthColumns = await db.Database.SqlQueryRaw<string>("SELECT COLUMN_NAME AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'integration' AND TABLE_NAME = 'connection_health'").ToListAsync();
        AssertSuperset(healthColumns, "tenant_id", "inbound_count_date_utc", "completed_inbound_days_json");
    }

    [DatabaseFact]
    public async Task Every_integration_tenant_table_is_under_the_tenant_policy()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var expected = db.Model.GetEntityTypes()
            .Where(entity => typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            .Where(entity => entity.GetSchema() == IntegrationDbContext.SchemaName)
            .Select(entity => entity.GetTableName()!).ToHashSet();
        var covered = await db.Database.SqlQueryRaw<string>($"""
            SELECT OBJECT_NAME(p.target_object_id) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_{IntegrationDbContext.SchemaName}'
              AND p.predicate_type_desc = 'FILTER'
            """).ToListAsync();
        Assert.Equal(expected.OrderBy(value => value), covered.OrderBy(value => value));
        var blocks = await db.Database.SqlQueryRaw<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_{IntegrationDbContext.SchemaName}'
              AND p.predicate_type_desc = 'BLOCK'
            """).SingleAsync();
        Assert.Equal(expected.Count * 2, blocks);
        var expectedMigration = db.Model.GetEntityTypes()
            .Where(entity => typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            .Where(entity => entity.GetSchema() == "migration")
            .Select(entity => entity.GetTableName()!).ToHashSet();
        var coveredMigration = await db.Database.SqlQueryRaw<string>("""
            SELECT OBJECT_NAME(p.target_object_id) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_migration'
              AND p.predicate_type_desc = 'FILTER'
            """).ToListAsync();
        Assert.Equal(expectedMigration.OrderBy(value => value), coveredMigration.OrderBy(value => value));
        var migrationBlocks = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_migration'
              AND p.predicate_type_desc = 'BLOCK'
            """).SingleAsync();
        Assert.Equal(expectedMigration.Count * 2, migrationBlocks);
        Assert.Empty(db.ValidateTenantKeyConventions());
    }

    [DatabaseFact]
    public async Task Default_wait_is_fifteen_minutes_and_expiry_marks_the_late_path()
    {
        var tenant = await fixture.NewTenantAsync("sequence");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var inbound = TimeOrderedId.New(fixture.Clock.UtcNow);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.InboundMessages.Add(new InboundMessage
        {
            TenantId = tenant, InboundMessageId = inbound, MessageId = Guid.CreateVersion7(), ConnectionId = connection,
            ConnectorCode = "CX01", ErpEntity = "delivery", ErpExternalId = "D-LATE", ErpChangeMarker = "1",
            MarkerKind = "sequence", ContractVersion = "1.0", MessageType = "DeliveryPosted", RawPayload = "{}",
            CanonicalMessage = "{}", OccurredAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            ObservedAt = new DateTime(2026, 9, 1, 8, 1, 0, DateTimeKind.Utc), AcceptedAt = fixture.Clock.UtcNow,
            IsLatest = true, TranslationStatus = InboundTranslationStatus.Pending,
        });
        await db.SaveChangesAsync();
        var resequencer = scope.ServiceProvider.GetRequiredService<BoundedWaitResequencer>();
        var held = await resequencer.HoldAsync(inbound, connection, ["FIC-LATE"],
            new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(15), held.HoldUntil - fixture.Clock.UtcNow);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(16);
        Assert.Equal(1, await resequencer.ReleaseExpiredAsync(CancellationToken.None));
        var row = await db.ResequenceItems.SingleAsync(x => x.InboundMessageId == inbound);
        Assert.True(row.Late);
        Assert.Equal(InboundTranslationStatus.Late, (await db.InboundMessages.SingleAsync(x => x.InboundMessageId == inbound)).TranslationStatus);
    }

    private static void AssertSuperset(IReadOnlyCollection<string> actual, params string[] expected)
    {
        foreach (var table in expected) Assert.Contains(table, actual);
    }
}
