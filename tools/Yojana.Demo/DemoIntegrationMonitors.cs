using System.Text.Json;
using System.Security.Cryptography;
using Yojana.Integration.Connectors;
using Yojana.Integration.Relays;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Workers;
using Yojana.Integration.Security.Cryptography;

namespace Yojana.Demo;

public sealed partial class DemoLoader
{
    private async Task CreateIntegrationMonitorScenariosAsync(Guid mainId, CancellationToken ct)
    {
        using var scope = TenantScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<IntegrationDbContext>();
        await provider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(ct);
        var entry = await db.ConnectorCatalogue.SingleAsync(x => x.ConnectorCode == "CX01", ct);
        entry.DisplayName = "Business ERP";
        entry.Enabled = true;
        var records = new[] { ("deliveries", "DeliveryPosted"), ("invoices", "InvoicePosted"), ("goods_receipts", "GoodsReceiptPosted"), ("items", "ItemUpserted"), ("business_partners", "PartyUpserted") };
        foreach (var (id, label) in new[] { (mainId, "Asterra main ERP"), (DemoIds.FromName("integration:connection:test-company"), "Asterra test company"), (DemoIds.FromName("integration:connection:service-depot"), "Velmora service depot") })
        {
            var connection = await db.Connections.SingleOrDefaultAsync(x => x.ConnectionId == id, ct);
            if (connection is null)
            {
                connection = new Connection { TenantId = tenant, ConnectionId = id, ConnectorCode = "CX01", DisplayLabel = label, Status = ConnectionStatus.Paused, CreatedAt = DemoDates.At(today.AddDays(-120)) };
                db.Connections.Add(connection);
            }
            // Paused connections keep all monitor read views populated without scheduler remote work.
            connection.Status = ConnectionStatus.Paused;
            connection.DisplayLabel = label;
            connection.RegistrationCompleted = true;
            if (!await db.ConnectionGatewaySettings.AnyAsync(x => x.ConnectionId == id, ct))
                db.ConnectionGatewaySettings.Add(new ConnectionGatewaySetting { TenantId = tenant, ConnectionId = id, LegalEntityId = LegalEntityId, OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", ReadOnly = true });
            await provider.GetRequiredService<Cx01Settings>().SetAsync(id,
                new(new Uri("https://erp.example.invalid/"), "FICTIONAL_DEMO", "1000340", Transport: "site_relay"), ct);
            foreach (var (_, message) in records)
                if (!await db.ConnectionCapabilities.AnyAsync(x => x.ConnectionId == id && x.CanonicalMessage == message, ct))
                    db.ConnectionCapabilities.Add(new ConnectionCapability { TenantId = tenant, ConnectionId = id, CanonicalMessage = message, SupportLevel = CapabilitySupport.Native });
            var health = await db.ConnectionHealth.SingleOrDefaultAsync(x => x.ConnectionId == id, ct);
            if (health is null) { health = new ConnectionHealth { TenantId = tenant, ConnectionId = id }; db.ConnectionHealth.Add(health); }
            health.Status = "paused";
            health.CheckedUpTo = DemoDates.At(today.AddDays(-1), 18);
            health.LastInboundAt = health.CheckedUpTo;
            health.ReceivedToday = id == mainId ? 1842 : 412;
            health.ReceivedLast30Days = id == mainId ? 42870 : 12140;
            health.NotificationState = "paused";
            health.LastDailyCheckAt = DemoDates.At(today, 2);
            health.LastDailyCheckResult = id == mainId ? "mismatch" : "clean";
            health.DailyCheckDifferences = id == mainId ? 2 : 0;
            health.CallsUsed = 4210;
            health.CallsCap = 50000;
            health.CallAlertPercent = 80;
            health.OpenDeadLetters = await db.DeadLetterItems.CountAsync(x => x.ConnectionId == id && x.Status == DeadLetterStatus.NeedsAttention, ct);
            health.OpenManualTasks = await db.ManualOutboundTasks.CountAsync(x => x.ConnectionId == id && x.Status == ManualTaskStatus.Open, ct);
            health.CredentialWarnDays = 14;
            health.CredentialReplaceBy = DemoDates.At(today.AddDays(3));
            health.RecordTypesJson = JsonSerializer.Serialize(records.Select((record, index) => new RecordTypeHealth(record.Item2, "paused", null, health.CheckedUpTo, 3600, 120, 300, 200 + index * 31, id == mainId ? index : 0, record.Item1)), IntegrationContracts.Json);
            health.UpdatedAt = DemoDates.At(today, 9);
            if (!await db.ConnectionEvents.AnyAsync(x => x.ConnectionId == id, ct))
                await provider.GetRequiredService<IConnectionAuditSink>().AppendAsync(new(tenant, Guid.NewGuid(), id, ConnectionAuditEventType.ConnectionPaused,
                    "system:demo", DemoDates.At(today.AddDays(-1), 18), JsonSerializer.Serialize(new { Reason = "Fictional demonstration connection; no remote calls are made." })), ct);
        }
        await db.SaveChangesAsync(ct);
        var profiles = provider.GetRequiredService<IConnectionThrottleProfileStore>();
        foreach (var id in new[] { mainId, DemoIds.FromName("integration:connection:test-company"), DemoIds.FromName("integration:connection:service-depot") })
            if (await profiles.GetAsync(new(tenant, id), ct) is null)
                await provider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(new(tenant, id),
                    new("CX01", new(null, 120, 50000, 100, 2, TimeSpan.FromMinutes(5))), ct);
        var relayId = DemoIds.FromName("integration:relay:warehouse");
        if (!await db.SiteRelays.AnyAsync(x => x.RelayId == relayId, ct))
            db.SiteRelays.Add(new SiteRelay { TenantId = tenant, RelayId = relayId, ConnectionId = mainId,
                DisplayName = "Central warehouse relay", Status = "active", CreatedAt = DemoDates.At(today.AddDays(-60)),
                LastSeenAt = DemoDates.At(today.AddDays(-1), 18), AgentVersion = "1.0.0", HostName = "ASTERRA-WH-EDGE",
                SigningKeyEnvelope = await provider.GetRequiredService<RelaySigningKeyProtection>().EncryptAsync(tenant,
                    mainId, relayId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ct) });
        var purpose = provider.GetRequiredKeyedService<IConnectionLifecycleAdapter>("CX01").CredentialPurposes[0];
        var credentialStore = provider.GetRequiredService<IConnectionCredentialStore>();
        if (await credentialStore.GetCurrentAsync(tenant, mainId, purpose, ct) is null)
        {
            // Random, unusable fixture bytes provide rotation metadata; there is no real remote identity here.
            var secret = RandomNumberGenerator.GetBytes(32);
            try { await credentialStore.SaveAsync(await provider.GetRequiredService<ConnectionCredentialProtector>().EncryptAsync(
                tenant, mainId, purpose, secret, DemoDates.At(today.AddDays(17)), DemoDates.At(today.AddDays(3)), ct), ct); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        var runId = DemoIds.FromName("integration:daily-check:latest");
        if (!await db.ReconciliationRuns.AnyAsync(x => x.ReconciliationRunId == runId, ct))
        {
            db.ReconciliationRuns.Add(new ReconciliationRun { TenantId = tenant, ReconciliationRunId = runId, ConnectionId = mainId, RangeStart = today.AddDays(-1), RangeEnd = today.AddDays(-1), Trigger = ReconciliationTrigger.Scheduled, Status = ReconciliationRunStatus.Mismatch, StartedAt = DemoDates.At(today, 2), CompletedAt = DemoDates.At(today, 2, 6), MismatchCount = 1, LocationMismatchCount = 1 });
            foreach (var (entity, message) in records)
            {
                var mismatch = entity == "deliveries";
                db.ReconciliationEntityDayResults.Add(new ReconciliationEntityDayResult { TenantId = tenant, ReconciliationEntityDayResultId = DemoIds.FromName("integration:comparison:" + entity), ReconciliationRunId = runId, ConnectionId = mainId, Entity = message, BusinessDate = today.AddDays(-1), SourceCount = 405, ModuleCount = 405, SourceQuantity = mismatch ? 2906 : 3118, ModuleQuantity = mismatch ? 2904 : 3118, SourceDistinctSerials = 2488, ModuleDistinctSerials = 2488, SourceKeyFieldHash = new string('a', 64), ModuleKeyFieldHash = new string(mismatch ? 'b' : 'a', 64), Status = mismatch ? "mismatch" : "clean", RangedReadSupported = true });
            }
            foreach (var (code, name, count) in new[] { ("NX-4850", "48-port network switch", 27), ("RS-1100", "Rack server", 9) })
                db.ReconciliationLocationResults.Add(new ReconciliationLocationResult { TenantId = tenant, ReconciliationLocationResultId = DemoIds.FromName("integration:stock-check:" + code), ReconciliationRunId = runId, ConnectionId = mainId, ItemId = Item(code).Id, LocationId = WarehouseId, ItemCode = code, ItemName = name, LocationCode = "LDS-WA-01", LocationName = "Central hardware warehouse", SourceOnHand = count + (code == "NX-4850" ? 1 : 0), ModuleUnitCount = count, Status = code == "NX-4850" ? "mismatch" : "clean" });
        }
        foreach (var (entity, status, count, total) in new[] { ("deliveries", "paused", 6124, 9480), ("invoices", "awaiting_reconciliation", 7013, 7013), ("goods_receipts", "completed", 120, 120) })
        {
            var id = DemoIds.FromName("integration:history:" + entity);
            if (await db.BackfillCursors.AnyAsync(x => x.BackfillCursorId == id, ct)) continue;
            db.BackfillCursors.Add(new BackfillCursor { TenantId = tenant, BackfillCursorId = id, ConnectionId = mainId, Entity = entity, RangeStart = today.AddDays(-90), RangeEnd = today.AddDays(-1), Cursor = "2026-09-01T14:22:07Z|412887", PageSize = 100, Status = status, RequestedBy = "system:demo", LastChangedBy = "system:demo", ProcessedMessages = count, ProcessedPages = (count + 99) / 100, TotalMessages = total, CreatedAt = DemoDates.At(today.AddDays(-3), 18), StartedAt = DemoDates.At(today.AddDays(-3), 18), LastPageAt = DemoDates.At(today.AddDays(-1), 18), AwaitingReconciliationAt = status != "paused" ? DemoDates.At(today.AddDays(-1), 19) : null, CompletedAt = status == "completed" ? DemoDates.At(today, 2, 6) : null, CompletionReconciliationRunId = status == "completed" ? runId : null, LastFailure = status == "paused" ? "Paused for the demonstration; resumes from its saved place when connected." : null });
        }
        await db.SaveChangesAsync(ct);
    }
}
