using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T14: the standing report is a paged set query and its snapshot is tenant data.</summary>
[Collection("serialisation host")]
public sealed class EntitlementGapReportDatabaseTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Cover_closure_drop_ship_filters_paging_export_and_snapshot_agree()
    {
        var tenant = await fixture.NewTenantAsync("gap");
        var item = Guid.NewGuid(); var customer = Guid.NewGuid(); var obligor = Guid.NewGuid();
        var uncovered = await SoldAsync(tenant, item, customer, obligor, UnitOrigin.Purchased, "GAP-1", EpisodeSource.Delivery, "delivery", 1);
        var live = await SoldAsync(tenant, item, customer, obligor, UnitOrigin.Purchased, "GAP-2", EpisodeSource.Invoice, "invoice", 2);
        var expired = await SoldAsync(tenant, item, customer, obligor, UnitOrigin.Purchased, "GAP-3", EpisodeSource.Delivery, "delivery", 3);
        var voided = await SoldAsync(tenant, item, customer, obligor, UnitOrigin.Purchased, "=GAP-4", EpisodeSource.Delivery, "delivery", 4);
        var drop = await SoldAsync(tenant, item, customer, obligor, UnitOrigin.VendorAsn, "GAP-5", EpisodeSource.CarrierPod, BackfillStartBasis.VendorShipConfirmation, 5);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Entitlements.AddRange(Cover(tenant, live, null), Cover(tenant, expired, EntitlementClosedReason.Expired), Cover(tenant, voided, EntitlementClosedReason.Voided));
            var episode = await db.Episodes.SingleAsync(e => e.UnitId == uncovered);
            db.EntitlementRequirements.Add(new EntitlementRequirement { TenantId = tenant, RequirementId = Guid.NewGuid(), UnitId = uncovered,
                OwnershipEpisodeId = episode.EpisodeId, ItemId = item, Status = EntitlementRequirementStatus.Exception,
                MissingInput = EntitlementMissingInput.NoPolicy, RaisedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<EntitlementGapQueries>();
            var all = await queries.PageAsync(new(Sort: "serial"), 0, 20, CancellationToken.None);
            Assert.Equal(3, all.Total);
            Assert.Equal(new HashSet<Guid> { uncovered, voided, drop }, all.Rows.Select(r => r.UnitId).ToHashSet());
            Assert.Equal(EntitlementMissingInput.NoPolicy, all.Rows.Single(r => r.UnitId == uncovered).MissingInput);
            Assert.True(all.Rows.Single(r => r.UnitId == drop).DropShip);
            Assert.StartsWith("'", EntitlementGapCsv.Row(all.Rows.Single(r => r.UnitId == voided)).Split(',')[1]);

            var onlyDrop = await queries.PageAsync(new(ItemIds: [item], SaleFrom: new DateOnly(2026, 5, 1), SaleTo: new DateOnly(2026, 5, 31),
                Channels: [EpisodeSource.CarrierPod], DropShip: EntitlementGapDropShip.Only), 0, 1, CancellationToken.None);
            Assert.Equal(drop, Assert.Single(onlyDrop.Rows).UnitId);
            var first = await queries.PageAsync(new(Sort: "sale_date_asc"), 0, 1, CancellationToken.None);
            var second = await queries.PageAsync(new(Sort: "sale_date_asc"), 1, 1, CancellationToken.None);
            Assert.Equal(3, first.Total); Assert.Equal(3, second.Total); Assert.NotEqual(first.Rows[0].UnitId, second.Rows[0].UnitId);

            var run = await RunEntitlementGapReportHandler.Handle(new RunEntitlementGapReport(Guid.NewGuid(), EntitlementGapTrigger.MigrationLoad, new()),
                queries, scope.ServiceProvider.GetRequiredService<SerialisationDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITenantContext>(), scope.ServiceProvider.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>(), CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
            Assert.Equal(all.Total, run.Total);
            Assert.True(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementGapRuns.AnyAsync(r => r.RunId == run.RunId));
        }
    }

    private async Task<Guid> SoldAsync(TenantId tenant, Guid item, Guid customer, Guid obligor, string origin, string serial, string source, string documentType, int day)
    {
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var commissioned = await bus.InvokeAsync<CommissionUnitsResult>(new CommissionUnits(Guid.NewGuid(), item, origin,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], new DateTime(2026, 5, day, 8, 0, 0, DateTimeKind.Utc), "test", obligor),
            new DeliveryOptions { TenantId = tenant.ToString() });
        var unit = commissioned.Lines[0].UnitId!.Value;
        await bus.InvokeAsync<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), unit, customer, PartyType.Customer, EpisodeReason.Sale,
            new DateTime(2026, 5, day, 9, 0, 0, DateTimeKind.Utc), "test", obligor, source, Document: new DocumentRef(documentType, Guid.NewGuid(), 1)),
            new DeliveryOptions { TenantId = tenant.ToString() });
        return unit;
    }

    private static Entitlement Cover(TenantId tenant, Guid unit, string? closedReason) => new()
    {
        TenantId = tenant, EntitlementId = Guid.NewGuid(), UnitId = unit, Scope = EntitlementScope.Unit, Type = EntitlementType.Manufacturer,
        CoverageFamily = CoverageFamilyCode.Base, Direction = EntitlementDirection.Outbound, ObligorEntityId = Guid.NewGuid(), PolicyVersionId = Guid.NewGuid(),
        StartTrigger = StartTrigger.Deliver, StartBasis = StartTrigger.Deliver, StartTimeZone = "UTC", StartDate = new DateOnly(2026, 5, 1), EndDate = new DateOnly(2027, 4, 30),
        DurationValue = 12, DurationUnit = "months", EndOfMonthRule = "clamp", DurationBasis = DurationBasis.Time, Transferable = Transferability.Yes,
        AssuranceOrService = AssuranceOrService.Assurance, Status = closedReason is null ? EntitlementStatus.Active : EntitlementStatus.Closed,
        ClosedReason = closedReason, ClosedAt = closedReason is null ? null : DateTime.UtcNow, Confidence = Confidence.High,
        Provenance = EntitlementProvenance.Created, CreatedEventId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
    };
}
