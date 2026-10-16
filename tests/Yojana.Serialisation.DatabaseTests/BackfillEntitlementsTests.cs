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

/// <summary>P2-T10, case 127 and tickets T21/T22: approved, atomic backfill through the bus.</summary>
[Collection("serialisation host")]
public sealed class BackfillEntitlementsTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly AmendmentCommitter Manager = new("service-manager", [YojanaRole.ServiceManager]);
    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(TenantId Tenant, Guid Item, Guid DeliveryUnit, Guid DropShipUnit, Guid CoveredUnit)> SetUpAsync()
    {
        var tenant = await fixture.NewTenantAsync("bf");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("BF-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReplacementPolicyCode = "BF-90" };
        var code = "BF-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2020, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        // These are deliberately historical gaps; automatic sale creation is exercised by EntitlementRequirementTests.
        Ports.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, false));
        var units = new List<Guid>();
        foreach (var origin in new[] { UnitOrigin.Purchased, UnitOrigin.VendorAsn, UnitOrigin.Purchased })
        {
            var created = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, origin,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "BF-" + Guid.NewGuid().ToString("N"))])], new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "test", Obligor));
            units.Add(created.Lines[0].UnitId!.Value);
        }

        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), units[0], Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            new DateTime(2026, 2, 3, 23, 30, 0, DateTimeKind.Utc), "sales", Obligor, EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 1)));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), units[1], Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            new DateTime(2026, 3, 4, 10, 15, 0, DateTimeKind.Utc), "purchasing", Obligor, EpisodeSource.CarrierPod, Document: new DocumentRef("vendor_ship_confirmation", Guid.NewGuid(), 2)));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), units[2], Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale,
            new DateTime(2026, 4, 5, 9, 0, 0, DateTimeKind.Utc), "sales", Obligor, EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 3)));
        await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), units[2], CoverageFamilyCode.Base, new DateOnly(2026, 4, 5), Obligor,
            new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc), "desk", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = new(2026, 4, 5, 9, 0, 0, DateTimeKind.Utc) },
            OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer));
        return (tenant, item, units[0], units[1], units[2]);
    }

    [DatabaseFact]
    public async Task Delivery_and_vendor_confirmation_start_backfills_publish_once_and_replay_idempotently()
    {
        var setup = await SetUpAsync();
        var command = new BackfillEntitlements(Guid.NewGuid(), [setup.DeliveryUnit, setup.DropShipUnit], BackfillReason.HistoricalGap, ["DN-42", "carrier confirmation"],
            new DocumentRef("backfill_batch", Guid.NewGuid()), Obligor, new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc), "service-manager", Manager, FinanceReviewRef: "FIN-77");
        var first = await InvokeAsync<BackfillEntitlementsResult>(setup.Tenant, command);
        Assert.Equal(["created", "created"], first.Units.Select(u => u.Outcome));
        Assert.Equal((new DateOnly(2026, 2, 3), new DateOnly(2026, 3, 4)), (first.Units[0].StartDate, first.Units[1].StartDate));
        var replay = await InvokeAsync<BackfillEntitlementsResult>(setup.Tenant, command);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Units.Select(u => u.EntitlementId), replay.Units.Select(u => u.EntitlementId));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
            .Where(e => e.UnitId == setup.DeliveryUnit || e.UnitId == setup.DropShipUnit).OrderBy(e => e.UnitId).ToListAsync();
        Assert.All(rows, e => Assert.Equal(EntitlementProvenance.Backfilled, e.Provenance));
        Assert.Contains(rows, e => e.UnitId == setup.DeliveryUnit && e.StartBasis == BackfillStartBasis.DeliveryDocument);
        Assert.Contains(rows, e => e.UnitId == setup.DropShipUnit && e.StartBasis == BackfillStartBasis.VendorShipConfirmation);

        var published = await PublishedEventSink.WaitForAsync<EntitlementBackfilled>(e => e.Header.UnitId == setup.DeliveryUnit || e.Header.UnitId == setup.DropShipUnit, 2, TimeSpan.FromSeconds(20));
        Assert.Equal(2, published.Count);
        Assert.All(published, e => Assert.True(e.FinanceReviewRequired));
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<EntitlementCreated>(), e => e.Header.UnitId == setup.DeliveryUnit || e.Header.UnitId == setup.DropShipUnit);
    }

    [DatabaseFact]
    public async Task Covered_units_refuse_the_list_unless_skip_invalid_is_explicit_and_finance_review_is_required()
    {
        var setup = await SetUpAsync();
        var command = new BackfillEntitlements(Guid.NewGuid(), [setup.DeliveryUnit, setup.CoveredUnit], BackfillReason.MigrationGap, ["migration row 81"],
            new DocumentRef("migration", Guid.NewGuid()), Obligor, new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc), "service-manager", Manager);
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<BackfillEntitlementsResult>(setup.Tenant, command));
        Assert.Equal("backfill_validation_failed", refused.Rule);
        var finance = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<BackfillEntitlementsResult>(setup.Tenant, command with { CommandId = Guid.NewGuid(), SkipInvalid = true }));
        Assert.Equal("finance_review_required", finance.Rule);
        var result = await InvokeAsync<BackfillEntitlementsResult>(setup.Tenant, command with { CommandId = Guid.NewGuid(), SkipInvalid = true, FinanceReviewRef = "FIN-77" });
        Assert.Equal(("created", (string?)null), (result.Units[0].Outcome, result.Units[0].Reason));
        Assert.Equal(("skipped", "already_has_base"), (result.Units[1].Outcome, result.Units[1].Reason));
    }
}
