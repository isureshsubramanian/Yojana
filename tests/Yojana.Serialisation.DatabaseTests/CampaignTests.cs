using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P4-T07: criteria, immutable recipient snapshots, re-selection, publication and tenant isolation.</summary>
[Collection("serialisation host")]
public sealed class CampaignTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, string serial, Guid item, Guid? lot = null)
    {
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)], LotId: lot)], At, "test", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return result.Lines[0].UnitId!.Value;
    }

    private Task<CampaignResult> CampaignAsync(TenantId tenant, string code, params CampaignCriterionInput[] criteria)
    {
        var id = Guid.NewGuid();
        return InvokeAsync<CampaignResult>(tenant, new CreateCampaign(id, code, "Campaign " + code,
            CampaignCoverageSource.Recall, "recall:" + code, criteria, At.AddDays(10), "service-manager",
            CampaignReason.Safety, null, null));
    }

    [DatabaseFact]
    public async Task Identifier_ranges_and_lots_are_or_within_kind_and_and_between_kinds()
    {
        var tenant = await fixture.NewTenantAsync("ca");
        var item = Guid.NewGuid();
        var lotA = Guid.NewGuid();
        var lotB = Guid.NewGuid();
        var outsideLot = Guid.NewGuid();
        var first = await UnitAsync(tenant, "11000001", item, lotA);
        var second = await UnitAsync(tenant, "11000002", item, lotB);
        await UnitAsync(tenant, "11000003", item, outsideLot);
        await UnitAsync(tenant, "12000001", item, lotA);
        var campaign = await CampaignAsync(tenant, "RANGE-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.IdentifierRange, Scheme: "internal", RangeStart: "11000001", RangeEnd: "11000003"),
            new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lotA),
            new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lotB));

        var preview = await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(campaign.Campaign.CampaignId, Take: 20));

        Assert.Equal(2, preview.Page.Total);
        Assert.Equal(new[] { first, second }.Order(), preview.Page.Items.Select(row => row.UnitId).Order());
    }

    [DatabaseFact]
    public async Task Child_unit_and_child_item_rows_are_alternatives_of_the_child_kind()
    {
        var tenant = await fixture.NewTenantAsync("cc");
        var item = Guid.NewGuid();
        var childItem = Guid.NewGuid();
        var parentByUnit = await UnitAsync(tenant, "21000001", item);
        var parentByItem = await UnitAsync(tenant, "21000002", item);
        var child = await UnitAsync(tenant, "21000003", childItem);
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parentByUnit, [new ChildInput(child)], At.AddDays(1), "line", Entity));
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parentByItem,
            [new ChildInput(null, childItem, Guid.NewGuid(), 1)], At.AddDays(1), "line", Entity));
        var campaign = await CampaignAsync(tenant, "CHILD-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.Child, ChildUnitId: child),
            new CampaignCriterionInput(CampaignCriterionKind.Child, ChildItemId: childItem));

        var preview = await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(campaign.Campaign.CampaignId, Take: 20));

        Assert.Equal(2, preview.Page.Total);
        Assert.Equal(new[] { parentByUnit, parentByItem }.Order(), preview.Page.Items.Select(row => row.UnitId).Order());
    }

    [DatabaseFact]
    public async Task Firmware_reads_current_value_or_the_value_effective_at_the_criterion_time()
    {
        var tenant = await fixture.NewTenantAsync("cf");
        var unit = await UnitAsync(tenant, "31000001", Guid.NewGuid());
        await FirmwareAsync(tenant, unit, "1.4.0", At.AddDays(1));
        await FirmwareAsync(tenant, unit, "2.0.0", At.AddDays(3));
        var historical = await CampaignAsync(tenant, "FW-H-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.Firmware, FirmwareValue: "1.4.0", EffectiveAt: At.AddDays(2)));
        var current = await CampaignAsync(tenant, "FW-C-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.Firmware, FirmwareValue: "2.0.0"));

        Assert.Equal(1, (await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(historical.Campaign.CampaignId))).Page.Total);
        Assert.Equal(1, (await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(current.Campaign.CampaignId))).Page.Total);
    }

    [DatabaseFact]
    public async Task Selection_snapshots_current_owner_and_custodian_reselects_idempotently_publishes_and_is_tenant_private()
    {
        var tenant = await fixture.NewTenantAsync("cs");
        var other = await fixture.NewTenantAsync("co");
        var lot = Guid.NewGuid();
        var item = Guid.NewGuid();
        var unit = await UnitAsync(tenant, "41000001", item, lot);
        var invoiceCustomer = Guid.NewGuid();
        var currentOwner = Guid.NewGuid();
        var custodian = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, invoiceCustomer, PartyType.Distributor,
            EpisodeReason.Sale, At.AddDays(1), "sales", Entity, Document: new DocumentRef("invoice", Guid.NewGuid())));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, currentOwner, PartyType.EndUser,
            EpisodeReason.Sale, At.AddDays(2), "registration", Entity, Source: EpisodeSource.Registration));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.InTransit,
            EpisodeReason.Sale, At.AddDays(1), "shipping", Entity, PartyRef: Guid.NewGuid(), PartyType: PartyType.ThreePl));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.CustomerSite,
            EpisodeReason.Sale, At.AddDays(2), "carrier", Entity, LocationId: Guid.NewGuid(), PartyRef: custodian, PartyType: PartyType.Customer));
        var campaign = await CampaignAsync(tenant, "SELECT-" + Guid.NewGuid().ToString("N")[..6], new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lot));
        var firstCommand = Guid.NewGuid();

        var first = await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(firstCommand, campaign.Campaign.CampaignId, At.AddDays(11), Entity, "service-manager"));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<CampaignUnitsSelected>(
            e => e.CampaignId == campaign.Campaign.CampaignId && e.SelectionCommandId == firstCommand, 1, TimeSpan.FromSeconds(10)));
        var publishedUnit = Assert.Single(published.Units);
        Assert.Equal(currentOwner, publishedUnit.OwnerPartyRef);
        Assert.Equal(custodian, publishedUnit.CustodianPartyRef);
        Assert.NotEqual(invoiceCustomer, publishedUnit.OwnerPartyRef);
        Assert.Equal((CampaignReason.Safety, (DateOnly?)null, 100m, 100m, 100m),
            (published.ReasonCode, published.RunsUntil, published.PartsPct, published.LabourPct, published.TravelPct));
        Assert.Equal(1, first.Added);

        var next = await UnitAsync(tenant, "41000002", item, lot);
        var second = await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaign.Campaign.CampaignId, At.AddDays(12), Entity, "service-manager"));
        var replay = await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaign.Campaign.CampaignId, At.AddDays(13), Entity, "service-manager"));
        Assert.Equal(1, second.Added);
        Assert.Equal(0, replay.Added);
        Assert.True(replay.Replayed);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var selected = await db.CampaignUnits.OrderBy(u => u.UnitId).ToListAsync();
            Assert.Equal(new[] { unit, next }.Order(), selected.Select(row => row.UnitId).Order());
            Assert.Equal(currentOwner, selected.Single(row => row.UnitId == unit).OwnerPartyRef);
            Assert.Equal(custodian, selected.Single(row => row.UnitId == unit).CustodianPartyRef);
        }

        using (var scope = fixture.ScopeFor(other))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Empty(await db.Campaigns.Where(c => c.CampaignId == campaign.Campaign.CampaignId).ToListAsync());
            Assert.Empty(await db.CampaignCriteria.Where(c => c.CampaignId == campaign.Campaign.CampaignId).ToListAsync());
            Assert.Empty(await db.CampaignUnits.Where(c => c.CampaignId == campaign.Campaign.CampaignId).ToListAsync());
        }
    }

    [DatabaseFact]
    public async Task Campaign_list_includes_selected_unit_count_and_status_filter()
    {
        var tenant = await fixture.NewTenantAsync("cl");
        var lot = Guid.NewGuid();
        await UnitAsync(tenant, "51000001", Guid.NewGuid(), lot);
        var campaign = await CampaignAsync(tenant, "LIST-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lot));
        await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaign.Campaign.CampaignId, At.AddDays(11), Entity, "service-manager"));

        using var scope = fixture.ScopeFor(tenant);
        var page = await scope.ServiceProvider.GetRequiredService<CampaignQueries>()
            .ListAsync(CampaignStatus.Selected, 0, 20, CancellationToken.None);

        var row = Assert.Single(page.Items, value => value.CampaignId == campaign.Campaign.CampaignId);
        Assert.Equal(1, row.SelectedUnits);
        Assert.Equal(CampaignStatus.Selected, row.Status);
        Assert.Equal((CampaignReason.Safety, (DateOnly?)null), (row.ReasonCode, row.RunsUntil));
    }

    [DatabaseFact]
    public async Task Funnel_counts_apply_kinds_cumulatively_in_the_screen_order()
    {
        var tenant = await fixture.NewTenantAsync("cn");
        var item = Guid.NewGuid();
        var includedLot = Guid.NewGuid();
        await UnitAsync(tenant, "52000001", item, includedLot);
        await UnitAsync(tenant, "52000002", item, Guid.NewGuid());
        await UnitAsync(tenant, "53000001", item, includedLot);
        var campaign = await CampaignAsync(tenant, "FUNNEL-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.IdentifierRange, Scheme: "internal", RangeStart: "52000001", RangeEnd: "52000002"),
            new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: includedLot));

        using var scope = fixture.ScopeFor(tenant);
        var steps = await scope.ServiceProvider.GetRequiredService<CampaignQueries>()
            .FunnelAsync(campaign.Campaign.CampaignId, CancellationToken.None);

        Assert.Collection(Assert.IsAssignableFrom<IReadOnlyList<CampaignFunnelStep>>(steps),
            range => { Assert.Equal(CampaignCriterionKind.IdentifierRange, range.Kind); Assert.Equal(2, range.Count); },
            lot => { Assert.Equal(CampaignCriterionKind.Lot, lot.Kind); Assert.Equal(1, lot.Count); });
    }

    [DatabaseFact]
    public async Task Unit_filters_summary_and_invoice_party_use_selection_and_first_sale_facts()
    {
        var tenant = await fixture.NewTenantAsync("cu");
        var lot = Guid.NewGuid();
        var unit = await UnitAsync(tenant, "54000001", Guid.NewGuid(), lot);
        var invoiceParty = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, invoiceParty, PartyType.Distributor,
            EpisodeReason.Sale, At.AddDays(1), "sales", Entity, Document: new DocumentRef("invoice", Guid.NewGuid())));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, owner, PartyType.EndUser,
            EpisodeReason.Sale, At.AddDays(2), "registration", Entity, Source: EpisodeSource.Registration));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.InRepair,
            EpisodeReason.Return, At.AddDays(3), "service", Entity));
        var campaign = await CampaignAsync(tenant, "UNITS-" + Guid.NewGuid().ToString("N")[..6],
            new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lot));
        var selectedAt = At.AddDays(11);
        await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaign.Campaign.CampaignId, selectedAt, Entity, "service-manager"));
        using (var writeScope = fixture.ScopeFor(tenant))
        {
            var db = writeScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Claims.Add(new Claim
            {
                TenantId = tenant, ClaimId = Guid.NewGuid(), ClaimNumber = "CLM-2026-00001", CommandId = Guid.NewGuid(), UnitId = unit,
                ClaimDate = DateOnly.FromDateTime(selectedAt), ClaimantPartyRef = owner, SettlementPartyRef = owner,
                ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "GB", Currency = "GBP",
                CoverageSource = CampaignCoverageSource.Recall, Remedy = ClaimRemedy.Repair, Status = ClaimStatus.Settled,
                SubmittedAt = selectedAt.AddHours(1), SubmittedBy = "service", LastEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<CampaignQueries>();
        var page = await queries.UnitsAsync(campaign.Campaign.CampaignId, "5400", owner, LocationKind.InRepair,
            true, "repaired", 0, 20, CancellationToken.None);
        var row = Assert.Single(Assert.IsAssignableFrom<Page<CampaignUnitDetailRow>>(page).Items);
        Assert.Equal(invoiceParty, row.InvoicePartyRef);
        Assert.Equal(owner, row.OwnerPartyRef);
        Assert.Equal(ClaimStatus.Settled, row.ClaimStatus);
        var summary = Assert.IsType<CampaignUnitsSummary>(await queries.UnitsSummaryAsync(campaign.Campaign.CampaignId, CancellationToken.None));
        Assert.Equal(1, summary.UnitsSelected);
        Assert.Equal(1, summary.OwnerDiffersFromInvoice);
        Assert.Equal(1, summary.Repaired);
        Assert.Equal(1, summary.InOurHands);
    }

    [Fact]
    public void Recipient_export_has_a_stable_csv_header()
    {
        Assert.Equal("serial,owner,custodian,selected_at\r\n", CampaignRecipientCsv.Header);
    }

    [DatabaseFact]
    public async Task Reason_is_required_and_unknown_codes_are_refused()
    {
        var tenant = await fixture.NewTenantAsync("cr");
        var criterion = new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: Guid.NewGuid());

        var required = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CampaignResult>(tenant,
            new CreateCampaign(Guid.NewGuid(), "NO-REASON", "No reason", CampaignCoverageSource.Recall, "recall:no-reason",
                [criterion], At, "service-manager", "", null, null)));
        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CampaignResult>(tenant,
            new CreateCampaign(Guid.NewGuid(), "BAD-REASON", "Bad reason", CampaignCoverageSource.Recall, "recall:bad-reason",
                [criterion], At, "service-manager", "other", null, null)));

        Assert.Equal("campaign_reason_required", required.Rule);
        Assert.Equal("campaign_reason_unknown", unknown.Rule);
    }

    [DatabaseFact]
    public async Task Runs_until_is_inclusive_stops_later_selection_and_cannot_move_before_first_selection()
    {
        var tenant = await fixture.NewTenantAsync("ce");
        var lot = Guid.NewGuid();
        await UnitAsync(tenant, "61000001", Guid.NewGuid(), lot);
        var criterion = new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lot);
        var campaignId = Guid.NewGuid();
        var campaign = await InvokeAsync<CampaignResult>(tenant, new CreateCampaign(campaignId, "ENDS", "Ends",
            CampaignCoverageSource.Recall, "recall:ends", [criterion], At, "service-manager",
            CampaignReason.Regulatory, "Regulatory inspection", new DateOnly(2026, 9, 12)));

        await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaignId, At.AddDays(11), Entity, "service-manager"));
        var ended = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(Guid.NewGuid(), campaignId, At.AddDays(12), Entity, "service-manager")));
        var beforeSelection = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CampaignResult>(tenant,
            new UpdateCampaign(Guid.NewGuid(), campaignId, campaign.Campaign.Code, campaign.Campaign.Name,
                campaign.Campaign.CoverageSource, campaign.Campaign.ReserveTag, CampaignStatus.Selected, [criterion],
                At.AddDays(12), "service-manager", CampaignReason.Regulatory, "Regulatory inspection", new DateOnly(2026, 9, 11))));

        Assert.Equal("campaign_selection_ended", ended.Rule);
        Assert.Equal("campaign_runs_until_before_selection", beforeSelection.Rule);

        var moved = await InvokeAsync<CampaignResult>(tenant,
            new UpdateCampaign(Guid.NewGuid(), campaignId, campaign.Campaign.Code, campaign.Campaign.Name,
                campaign.Campaign.CoverageSource, campaign.Campaign.ReserveTag, CampaignStatus.Selected, [criterion],
                At.AddDays(12), "service-manager", CampaignReason.Regulatory, "Extended inspection", new DateOnly(2026, 10, 1)));
        Assert.Equal(new DateOnly(2026, 10, 1), moved.Campaign.RunsUntil);
        Assert.Equal("Extended inspection", moved.Campaign.ReasonNote);
    }

    private async Task FirmwareAsync(TenantId tenant, Guid unitId, string value, DateTime at)
    {
        await InvokeAsync<RecordUnitAttributesResult>(tenant, new RecordUnitAttributes(Guid.NewGuid(), unitId,
            new Dictionary<string, string?> { ["firmware"] = value }, at, "device-management", Entity));
    }
}
