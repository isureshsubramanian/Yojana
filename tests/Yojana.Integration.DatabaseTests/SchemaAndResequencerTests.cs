using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Ordering;
using Yojana.Platform.Contracts.Identifiers;

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
            "outbound_command", "external_id_map", "exception_task", "resequence_item", "dead_letter_item",
            "dead_letter_event", "manual_outbound_task");
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
