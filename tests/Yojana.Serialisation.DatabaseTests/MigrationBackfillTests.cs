using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P9-T09: migrated owners the load left without base cover appear on the gap report run after the load, and an
/// approved BackfillEntitlements starts their cover from the incumbent's delivery recorded on the ownership fact.
/// </summary>
[Collection("serialisation host")]
public sealed class MigrationBackfillTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly AmendmentCommitter Manager = new("service-manager", [YojanaRole.ServiceManager]);
    private static readonly DateTime Received = new(2024, 3, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Delivered = new(2024, 5, 20, 14, 30, 0, DateTimeKind.Utc);
    // The equipment card names the owner later than the delivery; cover still starts at the delivery.
    private static readonly DateTime CardOwnerFrom = new(2024, 6, 3, 9, 0, 0, DateTimeKind.Utc);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static MigratedUnit Sold(Guid item, string serial, MigratedDelivery? delivery) => new(
        Guid.CreateVersion7(), item, HistoryDepth.Full, Received,
        [new IdentifierInput(IdentifierType.InternalSerial, serial)],
        [
            new MigratedHistoryEvent(Received, "received", "source", "Goods receipt", 4101, "V-ARVEN", "WH-01", 1),
            new MigratedHistoryEvent(Delivered, "shipped", "source", "Delivery", 7702, "C-NANDIRA", "WH-01", -1),
        ],
        null, new MigratedOwnership(Guid.NewGuid(), PartyType.Customer, CardOwnerFrom, false, Delivery: delivery),
        ConditionGrades.New, "inferred",
        Certificate: new(Guid.NewGuid(), 1, CertificateProvenance.Migrated,
            ["The source did not retain the as-built production record."], ["source history and identifiers"]));

    [DatabaseFact]
    public async Task Migrated_owners_without_cover_are_reported_after_the_load_and_backfill_from_the_recorded_delivery()
    {
        var tenant = await fixture.NewTenantAsync("mgbf");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false,
            AssuranceOrService.Assurance));
        var code = "MBF-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2020, 1, 1), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, false));

        var prefix = "MB" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var withDelivery = Sold(item, prefix + "-001", new MigratedDelivery(Delivered, "Delivery", "7702"));
        var withoutDelivery = Sold(item, prefix + "-002", null);
        var load = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant,
            new ApplyMigratedUnits(load, Entity, "user:migration-lead", "migration:test", [withDelivery, withoutDelivery]));
        Assert.Equal(2, applied.Applied);
        var deliveredUnit = applied.Units.Single(u => u.PlanUnitId == withDelivery.PlanUnitId).UnitId!.Value;
        var undeliveredUnit = applied.Units.Single(u => u.PlanUnitId == withoutDelivery.PlanUnitId).UnitId!.Value;

        var report = await InvokeAsync<EntitlementGapRunResult>(tenant, new RunEntitlementGapReport(Guid.NewGuid(),
            EntitlementGapTrigger.MigrationLoad, new EntitlementGapFilters(Channels: [EpisodeSource.Migration])));
        Assert.Equal(EntitlementGapTrigger.MigrationLoad, report.Trigger);
        Assert.Equal(2, report.Total);

        var backfill = await InvokeAsync<BackfillEntitlementsResult>(tenant, new BackfillEntitlements(Guid.NewGuid(),
            [deliveredUnit, undeliveredUnit], BackfillReason.MigrationGap, ["incumbent delivery 7702"],
            new DocumentRef("backfill_batch", Guid.NewGuid()), Entity, new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc),
            "service-manager", Manager, SkipInvalid: true, FinanceReviewRef: "FIN-MIG-1"));
        var created = backfill.Units.Single(u => u.UnitId == deliveredUnit);
        Assert.Equal("created", created.Outcome);
        Assert.Equal(DateOnly.FromDateTime(Delivered), created.StartDate);
        var skipped = backfill.Units.Single(u => u.UnitId == undeliveredUnit);
        Assert.Equal(("skipped", "no_start_evidence"), (skipped.Outcome, skipped.Reason));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var row = await db.Entitlements.AsNoTracking().SingleAsync(e => e.UnitId == deliveredUnit);
            Assert.Equal((EntitlementProvenance.Backfilled, BackfillStartBasis.MigratedDelivery), (row.Provenance, row.StartBasis));
        }

        var after = await InvokeAsync<EntitlementGapRunResult>(tenant, new RunEntitlementGapReport(Guid.NewGuid(),
            EntitlementGapTrigger.OnDemand, new EntitlementGapFilters(Channels: [EpisodeSource.Migration])));
        Assert.Equal(1, after.Total);

        // Backfilled cover is a fact after the load: the rehearsal can no longer be rolled back silently (OD-53).
        var rollBack = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(load, "user:migration-lead"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, rollBack.Outcome);
        Assert.Contains(deliveredUnit, rollBack.TouchedUnitIds);
    }
}
