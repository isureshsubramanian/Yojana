using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class MigrationReconciliationAttributionTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Attribution_uses_command_document_and_run_links_and_ignores_unlinked_business_events()
    {
        var tenant = await fixture.NewTenantAsync("erpattr");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var port = scope.ServiceProvider.GetRequiredService<IMigrationReconciliationPort>();
        var runId = Guid.CreateVersion7();
        var resolutionCommand = Guid.CreateVersion7();
        var targetDocument = Guid.CreateVersion7();
        var business = Add(db, tenant, "SRV-BUSINESS-10", 1, null, null, null);
        var deliberate = Add(db, tenant, "SRV-RESOLUTION-10", 2, resolutionCommand, null, null);
        var target = Add(db, tenant, "SRV-TARGET-10", 3, null, "delivery", targetDocument);
        var runLinked = Add(db, tenant, "SRV-RUN-10", 4, Guid.CreateVersion7(), "erp_change", runId);
        await db.SaveChangesAsync();

        var attribution = new MigrationUnitHistoryAttribution(runId, [resolutionCommand], [targetDocument]);
        var counts = await port.CountAttributedUnitHistoryChangesAsync(attribution, default);
        Assert.Equal(1, counts.DeliberateUnits);
        Assert.Equal(2, counts.UnexpectedUnits);

        var deliberatePage = await port.AttributedUnitHistoryChangesAsync(attribution,
            MigrationUnitHistoryChangeKind.Deliberate, null, 10, default);
        Assert.Equal(deliberate, Assert.Single(deliberatePage.Items).UnitId);
        var unexpectedPage = await port.AttributedUnitHistoryChangesAsync(attribution,
            MigrationUnitHistoryChangeKind.Unexpected, null, 10, default);
        Assert.Equal(new[] { runLinked, target }.Order().ToArray(),
            unexpectedPage.Items.Select(row => row.UnitId).Order().ToArray());
        Assert.DoesNotContain(unexpectedPage.Items.Concat(deliberatePage.Items), row => row.UnitId == business);
    }

    private static Guid Add(SerialisationDbContext db, TenantId tenant, string identifier, long sequence,
        Guid? commandId, string? documentType, Guid? documentId)
    {
        var unitId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unitId, LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = At, CreatedEventId = eventId,
            CreatedEventTime = At,
        });
        db.Identifiers.Add(new UnitIdentifier
        {
            TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unitId,
            Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
            RawValue = identifier, NormalisedValue = identifier.Replace("-", "", StringComparison.Ordinal),
            ScopeKey = "server", ValidFrom = At, ActorRef = "fixture", RecordedTime = At,
            EventId = eventId,
        });
        db.Events.Add(new UnitEvent
        {
            TenantId = tenant, EventId = eventId, UnitId = unitId, Seq = sequence,
            EventType = UnitEventType.Commissioned, EventTime = At.AddMinutes(sequence),
            RecordedTime = At.AddMinutes(sequence), LegalEntityId = Guid.CreateVersion7(),
            ActorRef = "fixture", IdempotencyKey = Guid.NewGuid().ToString("N"),
            DocumentType = documentType, DocumentId = documentId,
            Attributes = commandId is null ? null : JsonSerializer.Serialize(new { command_id = commandId }),
        });
        return unitId;
    }
}
